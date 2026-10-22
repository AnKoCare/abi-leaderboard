import { Inject, Injectable, Logger, OnModuleDestroy, OnModuleInit } from '@nestjs/common';
import { LEADERBOARD_JOB_RUNNER, LEADERBOARD_OPTIONS } from '../leaderboard.constants';
import type { ResolvedLeaderboardOptions } from '../leaderboard.options';
import type { LeaderboardJobRunner } from '../ports';
import { writeIsolatingBadRows } from '../util/mysql';
import { LeaderboardStore } from './leaderboard.store';
import { PendingScore, ScoreQueueService } from './score-queue.service';

/** Gom lô dở của worker đã chết mỗi chừng này — thao tác rẻ (đọc một tập nhỏ). */
const RECOVER_EVERY_MS = 5 * 60_000;

/** Rút hàng đợi điểm xuống MySQL theo lô. Chỉ chạy trên tiến trình được chạy job nền. */
@Injectable()
export class ScoreFlushService implements OnModuleInit, OnModuleDestroy {
  private readonly logger = new Logger('LeaderboardScoreFlush');
  private timer: ReturnType<typeof setInterval> | null = null;
  private flushing = false;
  private lastRecoverAt = 0;

  constructor(
    private readonly queue: ScoreQueueService,
    private readonly store: LeaderboardStore,
    @Inject(LEADERBOARD_JOB_RUNNER) private readonly jobRunner: LeaderboardJobRunner,
    @Inject(LEADERBOARD_OPTIONS) private readonly options: ResolvedLeaderboardOptions,
  ) {}

  onModuleInit(): void {
    if (!this.options.writeBehind.enabled || !this.jobRunner.isJobRunner()) return;
    this.timer = setInterval(() => void this.tick(), this.options.writeBehind.flushIntervalMs);
    this.logger.log(
      `worker flush điểm chạy (mỗi ${this.options.writeBehind.flushIntervalMs}ms, lô ${this.options.writeBehind.batchSize})`,
    );
  }

  onModuleDestroy(): void {
    if (this.timer) clearInterval(this.timer);
    this.timer = null;
  }

  private async tick(): Promise<void> {
    if (this.flushing) return;
    this.flushing = true;
    try {
      if (Date.now() - this.lastRecoverAt > RECOVER_EVERY_MS) {
        this.lastRecoverAt = Date.now();
        const recovered = await this.queue.recoverOrphans();
        if (recovered > 0) this.logger.log(`gom lại ${recovered} điểm dở của worker cũ`);
      }
      await this.drain(this.options.writeBehind.maxRoundsPerTick);
    } catch (error) {
      this.logger.error(`flush điểm lỗi: ${(error as Error).message}`);
    } finally {
      this.flushing = false;
    }
  }

  /**
   * Rút hàng đợi ngay tới khi rỗng (hoặc hết `maxRounds` lô). Dùng cho test, cho
   * `LeaderboardService.refreshNow()` và cho host muốn ép ghi trước khi tắt.
   */
  async flushNow(maxRounds = 1000): Promise<void> {
    await this.drain(maxRounds);
  }

  private async drain(maxRounds: number): Promise<void> {
    for (let round = 0; round < maxRounds; round += 1) {
      const batch = await this.queue.claim(this.options.writeBehind.batchSize);
      // Dừng khi THỰC SỰ rỗng. Không dùng `batch.length < batchSize` làm dấu hiệu hết:
      // HSCAN trả về xấp xỉ COUNT nên điều kiện đó dừng nhầm ngay vòng đầu.
      if (batch.length === 0) return;

      // Khoá theo thứ tự cố định để hai container không deadlock chéo nhau.
      batch.sort((a, b) => (a.playerId < b.playerId ? -1 : a.playerId > b.playerId ? 1 : 0));

      const dropped: Array<{ entry: PendingScore; error: unknown }> = [];
      try {
        await writeIsolatingBadRows(
          batch,
          async (chunk) => {
            await this.store.upsertBatch(chunk);
          },
          (entry, error) => dropped.push({ entry, error }),
        );
      } catch (error) {
        // Lỗi tạm thời (khoá, mất kết nối): trả nguyên lô, lượt sau thử lại.
        await this.queue.requeue(batch);
        this.logger.warn(`ghi lô điểm lỗi, trả ${batch.length} bản về hàng đợi: ${(error as Error).message}`);
        return;
      }

      // Dòng hỏng vì chính dữ liệu của nó thì bỏ hẳn — trả về hàng đợi là nó kéo mọi
      // lô sau xuống đường ghi từng dòng, mãi mãi.
      for (const { entry, error } of dropped) {
        this.logger.warn(
          `bỏ điểm không ghi được playerId=${entry.playerId} uid=${entry.uid} score=${entry.score}: ${(error as Error).message}`,
        );
      }
      await this.queue.complete(batch);
    }
  }
}
