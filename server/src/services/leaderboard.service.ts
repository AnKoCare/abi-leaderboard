import {
  BadRequestException,
  ForbiddenException,
  Inject,
  Injectable,
  Logger,
  NotFoundException,
} from '@nestjs/common';
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
import type { LeaderboardPlayer, LeaderboardPlayerResolver, LeaderboardSeasonProvider } from '../ports';
import type {
  LeaderboardRankResult,
  LeaderboardSubmitResult,
  LeaderboardTopResponse,
  ScoreWrite,
} from '../types';
import { LeaderboardKeys } from '../util/redis-keys';
import { BoardService } from './board.service';
import { LeaderboardStore, toPoint } from './leaderboard.store';
import { RankService } from './rank.service';
import { ScoreFlushService } from './score-flush.service';
import { ScoreQueueService } from './score-queue.service';

/** Làm mới blocklist trong bộ nhớ mỗi chừng này. */
const BLOCKLIST_REFRESH_MS = 60_000;
const MAX_SCORE = 2_147_483_647;

/**
 * API của bảng xếp hạng — dùng cho controller của package VÀ cho code server của host
 * (đồng bộ quốc gia, xoá tài khoản, cấm gian lận, ghi điểm từ luồng khác…).
 */
@Injectable()
export class LeaderboardService {
  private readonly logger = new Logger('Leaderboard');
  private readonly keys: LeaderboardKeys;
  private blocked: Set<string> | null = null;
  private blockedAt = 0;

  constructor(
    private readonly store: LeaderboardStore,
    private readonly queue: ScoreQueueService,
    private readonly flush: ScoreFlushService,
    private readonly boards: BoardService,
    private readonly ranks: RankService,
    @Inject(LEADERBOARD_REDIS) private readonly redis: RedisClientType,
    @Inject(LEADERBOARD_OPTIONS) private readonly options: ResolvedLeaderboardOptions,
    @Inject(LEADERBOARD_PLAYER_RESOLVER) private readonly players: LeaderboardPlayerResolver,
    @Inject(LEADERBOARD_SEASON_PROVIDER) private readonly seasons: LeaderboardSeasonProvider,
  ) {
    this.keys = new LeaderboardKeys(options.redisKeyPrefix);
  }

  // ---- ghi điểm ----

  /** `POST /submit` — userId lấy từ request đã qua guard của host. */
  async submitFromRequest(
    userId: string,
    score: number,
    secondaryScore?: number,
  ): Promise<LeaderboardSubmitResult> {
    const player = await this.resolvePlayer(userId);
    if (!player) throw new NotFoundException('Player not found for current user');
    const fromClient =
      this.options.secondaryScore.enabled && this.options.secondaryScore.acceptFromClient
        ? secondaryScore ?? 0
        : 0;
    return this.submitForPlayer(player, score, fromClient);
  }

  /**
   * Ghi điểm cho một player từ code server của host (vd. đồng bộ Level khi lưu profile,
   * hoặc cộng điểm phụ từ module giải đấu). Điểm chỉ tăng: gửi số thấp hơn không đổi gì.
   *
   * Mặc định đi qua hàng đợi Redis nên trả về ngay; điểm lên bảng sau lượt flush kế tiếp
   * và lượt dựng bảng kế tiếp (tối đa ~20 phút với lịch mặc định).
   */
  async submitForPlayer(
    player: LeaderboardPlayer,
    score: number,
    secondaryScore = 0,
  ): Promise<LeaderboardSubmitResult> {
    if (!Number.isInteger(score) || score < 0 || score > MAX_SCORE) {
      throw new BadRequestException('score must be an integer between 0 and 2147483647');
    }
    if (await this.isUidBlocked(player.uid)) {
      throw new ForbiddenException('Leaderboard submit is blocked for this player');
    }

    const write: ScoreWrite = {
      playerId: player.playerId,
      uid: player.uid,
      score,
      countryCode: player.countryCode ?? null,
      secondaryScore: this.options.secondaryScore.enabled ? toPoint(secondaryScore) : 0,
      season: await this.currentSeason(),
    };

    if (this.options.writeBehind.enabled && (await this.queue.enqueue(write))) {
      return { ...this.resultOf(write), scoreUpdatedAt: new Date() };
    }

    // Tắt write-behind, hoặc Redis hỏng: ghi thẳng — thà chậm còn hơn mất điểm.
    await this.store.upsertBatch([write]);
    const saved = await this.store.findByPlayerId(player.playerId);
    if (!saved) return { ...this.resultOf(write), scoreUpdatedAt: new Date() };
    return {
      playerId: saved.playerId,
      uid: saved.uid,
      score: saved.score,
      secondaryScore: saved.secondaryScore,
      countryCode: saved.countryCode,
      scoreUpdatedAt: saved.scoreUpdatedAt,
    };
  }

  private resultOf(write: ScoreWrite): Omit<LeaderboardSubmitResult, 'scoreUpdatedAt'> {
    return {
      playerId: write.playerId,
      uid: write.uid,
      score: write.score,
      secondaryScore: write.secondaryScore,
      countryCode: write.countryCode,
    };
  }

  // ---- đồng bộ từ host ----

  /**
   * Gọi khi player đổi quốc gia. Truyền `userId` để xoá cache player của đường submit —
   * không xoá thì các lần submit trong một giờ tới vẫn mang quốc gia cũ và ghi đè lại.
   */
  async syncCountry(playerId: string, countryCode: string | null, userId?: string): Promise<void> {
    const cc = countryCode ? countryCode.trim().toLowerCase() : null;
    if (cc !== null && !this.options.countryCodes.has(cc)) {
      throw new BadRequestException(`countryCode không hợp lệ: ${countryCode}`);
    }
    await this.store.updateCountry(playerId, cc);
    if (userId) await this.redis.del(this.keys.player(userId)).catch(() => undefined);
  }

  /** BẮT BUỘC gọi khi xoá tài khoản: package không dùng khoá ngoại sang bảng player. */
  async removePlayer(playerId: string, userId?: string): Promise<void> {
    await this.queue.forget(playerId);
    await this.store.deleteByPlayerId(playerId);
    if (userId) await this.redis.del(this.keys.player(userId)).catch(() => undefined);
  }

  // ---- chống gian lận ----

  /**
   * Cấm uid lên bảng và đưa điểm của nó về 0. Điểm đang chờ trong hàng đợi cũng bị lọc
   * lúc flush. Bảng đang cache vẫn hiện uid này tới lượt dựng kế tiếp — muốn gỡ ngay thì
   * gọi `refreshNow()`.
   */
  async blockUid(uid: string, reason?: string): Promise<void> {
    await this.store.insertBlockedUid(uid, reason ?? null);
    await this.store.zeroScoresOfUid(uid);
    this.blocked?.add(uid);
  }

  async unblockUid(uid: string): Promise<void> {
    await this.store.deleteBlockedUid(uid);
    this.blocked?.delete(uid);
  }

  /**
   * Blocklist giữ trong bộ nhớ, làm mới mỗi phút. Ở Colony Flow bản cũ truy vấn bảng này
   * MỖI request — 666 triệu lượt, đứng thứ 12 về tổng thời gian toàn hệ thống.
   */
  async isUidBlocked(uid: string): Promise<boolean> {
    const now = Date.now();
    if (!this.blocked || now - this.blockedAt > BLOCKLIST_REFRESH_MS) {
      try {
        this.blocked = await this.store.loadBlockedUids();
        this.blockedAt = now;
      } catch (error) {
        this.logger.warn(`làm mới blocklist lỗi: ${(error as Error).message}`);
        if (!this.blocked) return false;
      }
    }
    return this.blocked.has(uid);
  }

  // ---- đọc ----

  /** Chuẩn hoá + kiểm tra mã board; 'ww' = World. */
  normalizeBoard(countryCode: string): string {
    const board = String(countryCode ?? '').trim().toLowerCase();
    if (board !== WORLD_BOARD && !this.options.countryCodes.has(board)) {
      throw new BadRequestException("countryCode must be 'ww' or a valid ISO 3166-1 alpha-2 code");
    }
    return board;
  }

  /** Body JSON đã serialize — controller trả thẳng, không parse/stringify lại. */
  async getTopSerialized(countryCode: string, limit?: number, offset?: number): Promise<string> {
    const board = this.normalizeBoard(countryCode);
    const size = Math.min(Math.max(1, Math.floor(limit ?? this.options.defaultPageSize)), this.options.topLimit);
    const skip = Math.min(Math.max(0, Math.floor(offset ?? 0)), this.options.topLimit - 1);
    return this.boards.getTopSerialized(board, size, skip);
  }

  async getTop(countryCode: string, limit?: number, offset?: number): Promise<LeaderboardTopResponse> {
    return JSON.parse(await this.getTopSerialized(countryCode, limit, offset)) as LeaderboardTopResponse;
  }

  async getRank(uid: string, countryCode: string): Promise<LeaderboardRankResult | null> {
    const board = this.normalizeBoard(countryCode);
    if (!this.options.uidPattern.test(String(uid ?? ''))) {
      throw new BadRequestException('uid không đúng định dạng');
    }
    return this.ranks.getRank(uid, board);
  }

  /**
   * Ghi hết hàng đợi rồi dựng lại ngay histogram và mọi bảng. Nặng (một lượt quét bảng +
   * dựng mọi board) — dùng cho trang admin / test, không gọi theo request người chơi.
   */
  async refreshNow(): Promise<{ histograms: number; boards: number }> {
    await this.flush.flushNow();
    const histograms = await this.ranks.refreshHistograms();
    const boards = await this.boards.refreshAll();
    return { histograms, boards };
  }

  // ---- nội bộ ----

  /** userId → player, cache Redis — đường submit không phải truy vấn bảng player. */
  private async resolvePlayer(userId: string): Promise<LeaderboardPlayer | null> {
    const key = this.keys.player(userId);
    try {
      const cached = await this.redis.get(key);
      if (cached) return JSON.parse(cached) as LeaderboardPlayer;
    } catch (error) {
      this.logger.warn(`đọc cache player lỗi: ${(error as Error).message}`);
    }
    const player = await this.players.findByUserId(userId);
    if (player) {
      await this.redis
        .setEx(key, this.options.cacheTtlSec, JSON.stringify(player))
        .catch(() => undefined);
    }
    return player;
  }

  private async currentSeason(): Promise<number> {
    if (!this.options.secondaryScore.enabled) return FIRST_SEASON;
    return this.seasons.currentSeason();
  }
}
