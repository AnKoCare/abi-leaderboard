import { Inject, Injectable, Logger, OnModuleInit } from '@nestjs/common';
import { SchedulerRegistry } from '@nestjs/schedule';
import { CronJob } from 'cron';
import type { RedisClientType } from 'redis';
import {
  FIRST_SEASON,
  LEADERBOARD_JOB_RUNNER,
  LEADERBOARD_OPTIONS,
  LEADERBOARD_REDIS,
  LEADERBOARD_SEASON_PROVIDER,
} from '../leaderboard.constants';
import type { ResolvedLeaderboardOptions } from '../leaderboard.options';
import type { LeaderboardJobRunner, LeaderboardSeasonProvider } from '../ports';
import { LeaderboardKeys } from '../util/redis-keys';
import { BoardService } from './board.service';
import { LeaderboardStore } from './leaderboard.store';
import { RankService } from './rank.service';

/**
 * Cron bảo trì: dựng sẵn bảng, dựng histogram tính hạng, reset điểm phụ khi sang mùa.
 *
 * Mỗi job có hai lớp khoá:
 *  - khoá riêng của job (SET NX, TTL ngắn hơn chu kỳ) để nhiều container không cùng nhận
 *    một lượt — TTL dài hơn chu kỳ thì các lượt sau thấy khoá còn sống và bỏ chạy;
 *  - `jobRunner.runExclusive` để job không chồng lên job nặng KHÁC. Ở Colony Flow, job
 *    chồng nhau từng làm MySQL ghi chậm 10–12 giây và p95 vọt 10 giây.
 *
 * Không chạy lúc khởi động: deploy cuốn chiếu giờ cao điểm mà mỗi container mới dựng lại
 * hết thì MySQL quá tải. Cache sống 1 giờ, container mới cứ chờ lịch.
 */
@Injectable()
export class LeaderboardMaintenanceService implements OnModuleInit {
  private readonly logger = new Logger('LeaderboardMaintenance');
  private readonly keys: LeaderboardKeys;
  private readonly running = new Set<string>();
  /** Mùa mà tiến trình này đã xác nhận reset xong — nhịp thường ngày không chạm Redis/DB. */
  private appliedSeason = 0;

  constructor(
    private readonly scheduler: SchedulerRegistry,
    private readonly boards: BoardService,
    private readonly ranks: RankService,
    private readonly store: LeaderboardStore,
    @Inject(LEADERBOARD_REDIS) private readonly redis: RedisClientType,
    @Inject(LEADERBOARD_OPTIONS) private readonly options: ResolvedLeaderboardOptions,
    @Inject(LEADERBOARD_JOB_RUNNER) private readonly jobRunner: LeaderboardJobRunner,
    @Inject(LEADERBOARD_SEASON_PROVIDER) private readonly seasons: LeaderboardSeasonProvider,
  ) {
    this.keys = new LeaderboardKeys(options.redisKeyPrefix);
  }

  onModuleInit(): void {
    const cron = this.options.cron;
    if (cron === false) return;
    this.register('abi-leaderboard-histograms', cron.histograms, () =>
      this.runLocked('histograms', 4 * 60, () => this.ranks.refreshHistograms()),
    );
    this.register('abi-leaderboard-top-boards', cron.topBoards, () =>
      this.runLocked('top-boards', 8 * 60, () => this.boards.refreshAll()),
    );
    if (this.options.secondaryScore.enabled && this.options.secondaryScore.seasonProvider) {
      this.register('abi-leaderboard-season', cron.season, () => this.syncSeason());
    }
  }

  private register(name: string, expression: string, run: () => Promise<void>): void {
    const job = new CronJob(expression, () => {
      void run();
    });
    this.scheduler.addCronJob(name, job);
    job.start();
  }

  private async runLocked(name: string, lockTtlSec: number, work: () => Promise<number>): Promise<void> {
    if (!this.jobRunner.isJobRunner() || this.running.has(name)) return;
    try {
      const ok = await this.redis.set(this.keys.lock(name), '1', { NX: true, EX: lockTtlSec });
      if (!ok) return;
    } catch (error) {
      this.logger.warn(`${name}: không lấy được khoá (${(error as Error).message}), bỏ lượt`);
      return;
    }

    this.running.add(name);
    try {
      await this.jobRunner.runExclusive(`leaderboard-${name}`, async () => {
        const started = Date.now();
        const count = await work();
        this.logger.log(`${name}: ${count} board trong ${Date.now() - started}ms`);
      });
    } catch (error) {
      this.logger.error(`${name} lỗi: ${(error as Error).message}`);
    } finally {
      this.running.delete(name);
    }
  }

  /**
   * Sang mùa mới: reset điểm phụ của người không còn hoạt động, rồi dựng lại ngay bảng và
   * histogram — không dựng thì đầu bảng còn hiện thứ hạng của mùa trước tới hết TTL.
   *
   * Chạy mỗi phút nhưng gần như luôn thoát ở dòng so sánh đầu tiên. Mốc "đã reset" chỉ
   * được ghi SAU khi mọi việc xong; hỏng giữa chừng thì phút sau làm lại (chạy lại an toàn).
   */
  async syncSeason(): Promise<void> {
    if (!this.jobRunner.isJobRunner() || this.running.has('season')) return;
    this.running.add('season');
    try {
      const season = await this.seasons.currentSeason();
      if (season === this.appliedSeason) return;

      const stored = Number(await this.redis.get(this.keys.seasonApplied())) || 0;
      if (stored >= season) {
        this.appliedSeason = season;
        return;
      }
      if (season <= FIRST_SEASON) {
        await this.redis.set(this.keys.seasonApplied(), String(season));
        this.appliedSeason = season;
        return;
      }

      const locked = await this.redis.set(this.keys.lock('season'), '1', { NX: true, EX: 10 * 60 });
      if (!locked) return;
      try {
        await this.jobRunner.runExclusive('leaderboard-season', async () => {
          const started = Date.now();
          const reset = await this.store.resetStaleSecondary(season);
          const histograms = await this.ranks.refreshHistograms();
          const boards = await this.boards.refreshAll();
          await this.redis.set(this.keys.seasonApplied(), String(season));
          this.appliedSeason = season;
          this.logger.log(
            `mùa ${stored} → ${season}: reset ${reset} dòng, dựng lại ${boards} bảng + ${histograms} histogram trong ${Date.now() - started}ms`,
          );
        });
      } finally {
        await this.redis.del(this.keys.lock('season')).catch(() => undefined);
      }
    } catch (error) {
      this.logger.error(`syncSeason lỗi: ${(error as Error).message}`);
    } finally {
      this.running.delete('season');
    }
  }
}
