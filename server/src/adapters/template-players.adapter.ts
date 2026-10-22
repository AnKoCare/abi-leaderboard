import { Inject, Injectable } from '@nestjs/common';
import { InjectDataSource } from '@nestjs/typeorm';
import type { DataSource } from 'typeorm';
import { LEADERBOARD_OPTIONS } from '../leaderboard.constants';
import type { ResolvedLeaderboardOptions } from '../leaderboard.options';
import type {
  LeaderboardPlayer,
  LeaderboardPlayerResolver,
  LeaderboardProfileProvider,
} from '../ports';

/**
 * Cài sẵn hai port cho bảng `players` của template game-backend:
 * `id`, `userId`, `uid` (10 chữ số), `countryCode`, `info` (JSON).
 *
 * Chỉ đọc, bằng SQL thô — không phụ thuộc entity `Player` của host.
 */
@Injectable()
export class TemplatePlayersAdapter implements LeaderboardPlayerResolver, LeaderboardProfileProvider {
  private readonly table: string;

  constructor(
    @InjectDataSource() private readonly dataSource: DataSource,
    @Inject(LEADERBOARD_OPTIONS) options: ResolvedLeaderboardOptions,
  ) {
    // Đã kiểm tra chỉ gồm [A-Za-z0-9_] lúc đọc option.
    this.table = options.templatePlayersTable;
  }

  async findByUserId(userId: string): Promise<LeaderboardPlayer | null> {
    const rows = await this.dataSource.query(
      `SELECT id, uid, countryCode FROM \`${this.table}\` WHERE userId = ? LIMIT 1`,
      [userId],
    );
    return rows[0] ? this.toPlayer(rows[0]) : null;
  }

  async findByUid(uid: string): Promise<LeaderboardPlayer | null> {
    const rows = await this.dataSource.query(
      `SELECT id, uid, countryCode FROM \`${this.table}\` WHERE uid = ? LIMIT 1`,
      [uid],
    );
    return rows[0] ? this.toPlayer(rows[0]) : null;
  }

  async getProfiles(playerIds: string[]): Promise<Map<string, Record<string, unknown>>> {
    const out = new Map<string, Record<string, unknown>>();
    if (playerIds.length === 0) return out;
    const rows = await this.dataSource.query(
      `SELECT id, info FROM \`${this.table}\` WHERE id IN (${playerIds.map(() => '?').join(',')})`,
      playerIds,
    );
    for (const row of rows) {
      const info = this.parseInfo(row.info);
      if (info) out.set(String(row.id), info);
    }
    return out;
  }

  private toPlayer(row: any): LeaderboardPlayer {
    return {
      playerId: String(row.id),
      uid: String(row.uid),
      countryCode: row.countryCode ? String(row.countryCode).toLowerCase() : null,
    };
  }

  /** Cột `info` có thể là JSON (mysql2 trả object) hoặc text chứa JSON. */
  private parseInfo(value: unknown): Record<string, unknown> | null {
    if (value == null) return null;
    if (typeof value === 'object') return value as Record<string, unknown>;
    try {
      const parsed = JSON.parse(String(value));
      return parsed && typeof parsed === 'object' ? parsed : null;
    } catch {
      return null;
    }
  }
}
