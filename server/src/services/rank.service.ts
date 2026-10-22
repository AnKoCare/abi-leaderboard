import { Inject, Injectable, Logger } from '@nestjs/common';
import type { RedisClientType } from 'redis';
import {
  FIRST_SEASON,
  LEADERBOARD_OPTIONS,
  LEADERBOARD_PLAYER_RESOLVER,
  LEADERBOARD_REDIS,
  LEADERBOARD_SEASON_PROVIDER,
  WORLD_BOARD,
} from '../leaderboard.constants';
import type { ResolvedLeaderboardOptions } from '../leaderboard.options';
import type { LeaderboardPlayerResolver, LeaderboardSeasonProvider } from '../ports';
import type { EntryRow, LeaderboardRankResult } from '../types';
import { LeaderboardKeys } from '../util/redis-keys';
import { currentSecondary } from './board.service';
import { LeaderboardStore } from './leaderboard.store';

/** Field chứa tổng số dòng của board trong hash histogram. */
const TOTAL_FIELD = '__total';

/**
 * Hạng của một uid.
 *
 * Đếm "bao nhiêu người đứng trên" bằng hai câu COUNT trên bảng hàng triệu dòng từng đẩy
 * p95 của `/rank` lên 4,6 giây ở Colony Flow. Vì điểm là Level (vài nghìn giá trị), cron
 * dựng sẵn cho mỗi board một hash histogram: field = điểm, value = số người có điểm CAO
 * HƠN. Request chỉ còn một HGET + một COUNT gói trong đúng nhóm cùng điểm.
 *
 * GIỚI HẠN: chỉ rẻ khi điểm có ít giá trị phân biệt. Game dùng điểm dải rộng (hàng triệu)
 * thì hash phình theo số giá trị — khi đó cần histogram theo dải điểm (chưa làm).
 */
@Injectable()
export class RankService {
  private readonly logger = new Logger('LeaderboardRanks');
  private readonly keys: LeaderboardKeys;
  /** Hash histogram phải sống qua NHIỀU nhịp cron: hết hạn là `/rank` quay về đếm thẳng. */
  private readonly histogramTtlSec: number;

  constructor(
    private readonly store: LeaderboardStore,
    @Inject(LEADERBOARD_REDIS) private readonly redis: RedisClientType,
    @Inject(LEADERBOARD_OPTIONS) private readonly options: ResolvedLeaderboardOptions,
    @Inject(LEADERBOARD_PLAYER_RESOLVER) private readonly players: LeaderboardPlayerResolver,
    @Inject(LEADERBOARD_SEASON_PROVIDER) private readonly seasons: LeaderboardSeasonProvider,
  ) {
    this.keys = new LeaderboardKeys(options.redisKeyPrefix);
    this.histogramTtlSec = Math.max(options.cacheTtlSec, 3600);
  }

  /** Null = uid không có trên board này. Cache cả kết quả null, cùng TTL với bảng. */
  async getRank(uid: string, board: string): Promise<LeaderboardRankResult | null> {
    const key = this.keys.rank(board, uid);
    try {
      const cached = await this.redis.get(key);
      if (cached !== null) return cached === '' ? null : (JSON.parse(cached) as LeaderboardRankResult);
    } catch (error) {
      this.logger.warn(`đọc cache hạng lỗi: ${(error as Error).message}`);
    }

    const result = await this.compute(uid, board);
    await this.redis
      .setEx(key, this.options.cacheTtlSec, result === null ? '' : JSON.stringify(result))
      .catch((error: Error) => this.logger.warn(`ghi cache hạng lỗi: ${error.message}`));
    return result;
  }

  async compute(uid: string, board: string): Promise<LeaderboardRankResult | null> {
    const entry = await this.store.findByUid(uid);
    if (!entry) return null;

    // Dòng chưa có quốc gia mà player đã chọn quốc gia: sửa luôn dòng đó.
    let country = entry.countryCode;
    if (!country && this.players.findByUid) {
      const player = await this.players.findByUid(uid);
      if (player?.countryCode) {
        country = player.countryCode;
        await this.store.updateCountry(entry.playerId, country);
      }
    }
    if (board !== WORLD_BOARD && country !== board) return null;

    const { total, better } = await this.count(entry, board);
    const season = this.options.secondaryScore.enabled
      ? await this.seasons.currentSeason()
      : FIRST_SEASON;
    return {
      rank: better + 1,
      uid: entry.uid,
      score: entry.score,
      secondaryScore: this.options.secondaryScore.enabled ? currentSecondary(entry, season) : 0,
      countryCode: country,
      board,
      total,
    };
  }

  /**
   * (số người đứng trên, tổng số người) của board. Histogram chưa có (cron chưa chạy,
   * Redis lỗi, hoặc điểm này mới xuất hiện sau lần dựng) thì đếm thẳng.
   */
  async count(entry: EntryRow, board: string): Promise<{ total: number; better: number }> {
    const histogram = await this.readHistogram(board, entry.score);
    if (histogram) {
      const sameScoreBetter = await this.store.countSameScoreBetter(entry, board);
      return { total: histogram.total, better: histogram.above + sameScoreBetter };
    }
    return this.store.countRankDirect(entry, board);
  }

  private async readHistogram(
    board: string,
    score: number,
  ): Promise<{ above: number; total: number } | null> {
    try {
      const [above, total] = await this.redis.hmGet(this.keys.histogram(board), [
        String(score),
        TOTAL_FIELD,
      ]);
      if (total == null || above == null) return null;
      return { above: Number(above), total: Number(total) };
    } catch (error) {
      this.logger.warn(`đọc histogram lỗi: ${(error as Error).message}`);
      return null;
    }
  }

  /**
   * Cron: dựng histogram cho MỌI board từ MỘT truy vấn `GROUP BY countryCode, score`.
   * Board World cộng dồn mọi nhóm, kể cả dòng chưa có quốc gia.
   */
  async refreshHistograms(): Promise<number> {
    const counts = await this.store.scoreCounts();
    const perBoard = new Map<string, Map<number, number>>();
    const world = new Map<number, number>();
    for (const { countryCode, score, count } of counts) {
      world.set(score, (world.get(score) ?? 0) + count);
      if (!countryCode) continue;
      let hist = perBoard.get(countryCode);
      if (!hist) {
        hist = new Map();
        perBoard.set(countryCode, hist);
      }
      hist.set(score, (hist.get(score) ?? 0) + count);
    }
    perBoard.set(WORLD_BOARD, world);

    let built = 0;
    for (const [board, hist] of perBoard) {
      try {
        // Duyệt từ điểm cao xuống và cộng dồn: giá trị của mỗi điểm = số người ĐỨNG TRÊN nó.
        const payload: Record<string, string> = {};
        let above = 0;
        for (const score of [...hist.keys()].sort((a, b) => b - a)) {
          payload[String(score)] = String(above);
          above += hist.get(score) ?? 0;
        }
        payload[TOTAL_FIELD] = String(above);

        const key = this.keys.histogram(board);
        await this.redis
          .multi()
          .del(key)
          .hSet(key, payload)
          .expire(key, this.histogramTtlSec)
          .exec();
        built += 1;
      } catch (error) {
        this.logger.warn(`dựng histogram ${board} lỗi: ${(error as Error).message}`);
      }
    }
    return built;
  }
}
