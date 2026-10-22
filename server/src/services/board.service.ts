import { Inject, Injectable, Logger } from '@nestjs/common';
import type { RedisClientType } from 'redis';
import {
  FIRST_SEASON,
  LEADERBOARD_OPTIONS,
  LEADERBOARD_PROFILE_PROVIDER,
  LEADERBOARD_REDIS,
  LEADERBOARD_SEASON_PROVIDER,
  WORLD_BOARD,
} from '../leaderboard.constants';
import type { LeaderboardProfileOptions, ResolvedLeaderboardOptions } from '../leaderboard.options';
import type { LeaderboardProfileProvider, LeaderboardSeasonProvider } from '../ports';
import type { EntryRow, LeaderboardTopEntry, LeaderboardTopResponse } from '../types';
import { yieldToEventLoop } from '../util/mysql';
import { LeaderboardKeys } from '../util/redis-keys';
import { LeaderboardStore, toPoint, toSeason } from './leaderboard.store';

/** Provider nhận tối đa chừng này id mỗi lần gọi. */
const PROFILE_BATCH = 500;

/**
 * Dựng `info` của một dòng từ profile: chỉ gồm key khai báo trong option — không spread
 * nguyên profile, tránh lọt dữ liệu riêng tư lên một API công khai được nginx cache.
 */
export function buildProfileInfo(
  profile: Record<string, unknown> | undefined,
  opts: Required<LeaderboardProfileOptions>,
): Record<string, unknown> {
  const merged: Record<string, unknown> = { ...opts.defaults, ...(profile ?? {}) };
  const info: Record<string, unknown> = {};
  for (const field of opts.fields) {
    info[field] = merged[field] ?? null;
  }
  for (const field of opts.optionalFields) {
    const value = merged[field];
    if (value != null && value !== 0 && value !== '' && value !== false) info[field] = value;
  }
  return info;
}

/**
 * Điểm phụ TÍNH THEO MÙA HIỆN HÀNH: dòng còn giữ điểm của mùa đã qua thì là 0. DB được
 * dọn sau lúc sang mùa một nhịp cron; trong khoảng đó không được khoe điểm cũ.
 */
export function currentSecondary(row: Pick<EntryRow, 'secondaryScore' | 'season'>, season: number): number {
  return toSeason(row.season) >= season ? toPoint(row.secondaryScore) : 0;
}

/**
 * Bảng top N của từng board.
 *
 * Cache hai tầng, cùng TTL (mặc định 1 giờ):
 *  - board: mảng JSON top N (`board`), dựng từ DB;
 *  - trang: body JSON đã serialize theo (offset, limit) (`page`). Đây là đường nóng —
 *    trúng cache chỉ tốn một lệnh GET, không parse board 200 KB, không stringify lại.
 *
 * Cron `refreshAll` dựng lại trước khi hết hạn nên request thường không phải tự truy vấn.
 * Ở Colony Flow, không có cron thì request xui đầu tiên sau mỗi giờ lãnh p95 1,8–2,9 giây.
 */
@Injectable()
export class BoardService {
  private readonly logger = new Logger('LeaderboardBoards');
  private readonly keys: LeaderboardKeys;
  /** Gộp các request cùng board lúc cache trượt — một truy vấn cho cả cụm. */
  private readonly inflight = new Map<string, Promise<LeaderboardTopEntry[]>>();

  constructor(
    private readonly store: LeaderboardStore,
    @Inject(LEADERBOARD_REDIS) private readonly redis: RedisClientType,
    @Inject(LEADERBOARD_OPTIONS) private readonly options: ResolvedLeaderboardOptions,
    @Inject(LEADERBOARD_PROFILE_PROVIDER) private readonly profiles: LeaderboardProfileProvider,
    @Inject(LEADERBOARD_SEASON_PROVIDER) private readonly seasons: LeaderboardSeasonProvider,
  ) {
    this.keys = new LeaderboardKeys(options.redisKeyPrefix);
  }

  /** Body JSON của một trang; caller đã kiểm tra board và chặn limit/offset. */
  async getTopSerialized(board: string, limit: number, offset: number): Promise<string> {
    const pageKey = this.keys.page(board, offset, limit);
    try {
      const cached = await this.redis.get(pageKey);
      if (cached) return cached;
    } catch (error) {
      this.logger.warn(`đọc cache trang lỗi: ${(error as Error).message}`);
    }

    const entries = await this.getBoard(board);
    const body = JSON.stringify(this.page(board, entries, limit, offset));
    await this.cachePage(board, pageKey, body);
    return body;
  }

  private page(
    board: string,
    entries: LeaderboardTopEntry[],
    limit: number,
    offset: number,
  ): LeaderboardTopResponse {
    const slice = entries.slice(offset, offset + limit);
    return { countryCode: board, total: slice.length, boardTotal: entries.length, entries: slice };
  }

  /** Trang hết hạn CÙNG LÚC với board; không thì dữ liệu có thể cũ tới 2×TTL. */
  private async cachePage(board: string, pageKey: string, body: string): Promise<void> {
    try {
      const boardTtlMs = await this.redis.pTTL(this.keys.board(board));
      if (boardTtlMs > 0) {
        await this.redis.pSetEx(pageKey, boardTtlMs, body);
      } else {
        await this.redis.setEx(pageKey, this.options.cacheTtlSec, body);
      }
    } catch (error) {
      this.logger.warn(`ghi cache trang lỗi: ${(error as Error).message}`);
    }
  }

  async getBoard(board: string): Promise<LeaderboardTopEntry[]> {
    try {
      const cached = await this.redis.get(this.keys.board(board));
      if (cached) return JSON.parse(cached) as LeaderboardTopEntry[];
    } catch (error) {
      this.logger.warn(`đọc cache bảng lỗi: ${(error as Error).message}`);
    }

    const pending = this.inflight.get(board);
    if (pending) return pending;

    const task = this.buildBoard(board)
      .then(async (entries) => {
        await this.redis
          .setEx(this.keys.board(board), this.options.cacheTtlSec, JSON.stringify(entries))
          .catch((error: Error) => this.logger.warn(`ghi cache bảng lỗi: ${error.message}`));
        return entries;
      })
      .finally(() => this.inflight.delete(board));
    this.inflight.set(board, task);
    return task;
  }

  /** Đọc top từ DB rồi nạp profile theo lô. Provider lỗi thì NÉM ra — không cache bảng thiếu profile cả giờ. */
  async buildBoard(board: string): Promise<LeaderboardTopEntry[]> {
    const rows = await this.store.loadTop(board, this.options.topLimit);
    const profiles = await this.loadProfiles(rows.map((r) => r.playerId));
    const season = this.options.secondaryScore.enabled
      ? await this.seasons.currentSeason()
      : FIRST_SEASON;

    return rows.map((row, index) => {
      const profile = profiles.get(row.playerId);
      const entry: LeaderboardTopEntry = {
        rank: index + 1,
        uid: row.uid,
        score: row.score,
        countryCode: row.countryCode,
        info: buildProfileInfo(profile, this.options.profile),
      };
      if (this.options.secondaryScore.enabled) {
        const secondary = currentSecondary(row, season);
        if (secondary > 0) entry.secondaryScore = secondary;
      }
      this.options.decorateEntry?.(entry, profile);
      return entry;
    });
  }

  private async loadProfiles(playerIds: string[]): Promise<Map<string, Record<string, unknown>>> {
    const out = new Map<string, Record<string, unknown>>();
    if (this.options.profile.fields.length === 0 && this.options.profile.optionalFields.length === 0) {
      // Không khai báo field nào thì không cần profile — khỏi gọi provider.
      if (!this.options.decorateEntry) return out;
    }
    for (let i = 0; i < playerIds.length; i += PROFILE_BATCH) {
      const chunk = await this.profiles.getProfiles(playerIds.slice(i, i + PROFILE_BATCH));
      for (const [id, profile] of chunk) out.set(id, profile);
    }
    return out;
  }

  /**
   * Cron: dựng lại board World và mọi board quốc gia đang có, kèm body trang mặc định.
   *
   * Nhường event loop TRƯỚC mỗi board: việc nặng nhất là CPU đồng bộ (dựng ~1.000 dòng
   * và JSON.stringify ~200 KB mỗi board), không phải chờ DB. Ở Colony Flow, 38 board
   * chạy liền một mạch chiếm event loop ~30 giây và p95 của cả app vọt lên 4,3 giây.
   */
  async refreshAll(): Promise<number> {
    const boards = [WORLD_BOARD, ...(await this.store.distinctCountries())];
    let built = 0;
    for (const board of boards) {
      await yieldToEventLoop();
      try {
        const entries = await this.buildBoard(board);
        await this.redis.setEx(this.keys.board(board), this.options.cacheTtlSec, JSON.stringify(entries));
        const body = JSON.stringify(this.page(board, entries, this.options.defaultPageSize, 0));
        await this.redis.setEx(
          this.keys.page(board, 0, this.options.defaultPageSize),
          this.options.cacheTtlSec,
          body,
        );
        built += 1;
      } catch (error) {
        this.logger.warn(`dựng bảng ${board} lỗi: ${(error as Error).message}`);
      }
    }
    return built;
  }
}
