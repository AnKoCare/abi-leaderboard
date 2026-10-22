import { Inject, Injectable, Logger } from '@nestjs/common';
import { InjectDataSource } from '@nestjs/typeorm';
import { randomUUID } from 'crypto';
import type { DataSource, QueryRunner } from 'typeorm';
import {
  BLOCKED_UIDS_TABLE,
  ENTRIES_TABLE,
  FIRST_SEASON,
  LEADERBOARD_OPTIONS,
  WORLD_BOARD,
} from '../leaderboard.constants';
import type { ResolvedLeaderboardOptions } from '../leaderboard.options';
import { WORLD_ORDER_INDEX } from '../migrations/LeaderboardSchema1790000000000';
import type { EntryRow, ScoreWrite } from '../types';
import {
  applyLockWaitTimeout,
  isMissingIndexError,
  runWithLockRetry,
  yieldToEventLoop,
} from '../util/mysql';

/** Số nguyên không âm; mọi thứ khác về 0. */
export function toPoint(value: unknown): number {
  const n = Number(value);
  return Number.isFinite(n) && n > 0 ? Math.floor(n) : 0;
}

/** Mùa ghi vào dòng; thiếu / rác = mùa đầu. */
export function toSeason(value: unknown): number {
  const n = Number(value);
  return Number.isFinite(n) && n >= FIRST_SEASON ? Math.floor(n) : FIRST_SEASON;
}

/**
 * Thứ tự xếp hạng. `id` ở cuối là chốt chặn khi trùng HOÀN TOÀN (cùng điểm, cùng điểm
 * phụ, cùng `rankedAt` — mọi dòng của một câu INSERT theo lô có chung NOW(6)). Thiếu nó
 * thì vị trí trong bảng do MySQL tự chọn còn hạng tính bằng COUNT lại cho cả nhóm cùng
 * một số, hai thứ lệch nhau.
 *
 * Thêm `id` vẫn đi trên index: InnoDB ngầm gắn khoá chính vào cuối mọi index phụ.
 * Phép đếm hạng (`betterThanSql`) phải khớp TỪNG CHỮ với thứ tự này.
 */
const ORDER_BY_SQL = 'score DESC, leaguePoint DESC, rankedAt ASC, id ASC';

const ROW_COLUMNS_SQL = `id, playerId, uid, score, leaguePoint AS secondaryScore,
  leagueCycle AS season, countryCode,
  DATE_FORMAT(rankedAt, '%Y-%m-%d %H:%i:%s.%f') AS rankedAt`;

@Injectable()
export class LeaderboardStore {
  private readonly logger = new Logger('LeaderboardStore');

  /**
   * ON DUPLICATE KEY UPDATE dùng chung cho mọi đường ghi.
   *
   * - `score` chỉ tăng (GREATEST); `scoreUpdatedAt` chỉ đổi khi score tăng.
   * - Điểm phụ (`leaguePoint`) của mùa (`leagueCycle`):
   *     mùa MỚI hơn dòng đang lưu → ghi đè, kể cả thấp hơn (đây là lúc reset);
   *     cùng mùa → GREATEST (lần gửi không mang điểm phụ, tức 0, không xoá số cũ);
   *     mùa CŨ hơn (request tới muộn) → bỏ qua, không hồi sinh điểm vừa reset.
   * - `rankedAt` = lúc đạt đủ cặp (score, điểm phụ) hiện tại. Bị reset về 0 điểm phụ thì
   *   cặp hiện tại đạt từ lúc lên score đó, nên lấy `scoreUpdatedAt` chứ không lấy NOW.
   *
   * THỨ TỰ CÁC DÒNG GÁN LÀ BẮT BUỘC: MySQL gán tuần tự và dòng sau thấy giá trị MỚI của
   * dòng trước. `rankedAt` và `leaguePoint` phải so với giá trị CŨ nên đứng trước các
   * dòng gán `score` / `leaguePoint` / `leagueCycle`.
   */
  static readonly UPSERT_ON_DUPLICATE_SQL = `
      uid = VALUES(uid),
      countryCode = COALESCE(VALUES(countryCode), ${ENTRIES_TABLE}.countryCode),
      scoreUpdatedAt = IF(VALUES(score) > ${ENTRIES_TABLE}.score, NOW(6), ${ENTRIES_TABLE}.scoreUpdatedAt),
      rankedAt = CASE
        WHEN VALUES(score) > ${ENTRIES_TABLE}.score THEN NOW(6)
        WHEN VALUES(leagueCycle) > ${ENTRIES_TABLE}.leagueCycle THEN
          IF(VALUES(leaguePoint) > 0, NOW(6), ${ENTRIES_TABLE}.scoreUpdatedAt)
        WHEN VALUES(leagueCycle) = ${ENTRIES_TABLE}.leagueCycle
          AND VALUES(leaguePoint) > ${ENTRIES_TABLE}.leaguePoint THEN NOW(6)
        ELSE ${ENTRIES_TABLE}.rankedAt
      END,
      score = GREATEST(${ENTRIES_TABLE}.score, VALUES(score)),
      leaguePoint = CASE
        WHEN VALUES(leagueCycle) > ${ENTRIES_TABLE}.leagueCycle THEN VALUES(leaguePoint)
        WHEN VALUES(leagueCycle) = ${ENTRIES_TABLE}.leagueCycle
          THEN GREATEST(${ENTRIES_TABLE}.leaguePoint, VALUES(leaguePoint))
        ELSE ${ENTRIES_TABLE}.leaguePoint
      END,
      leagueCycle = GREATEST(${ENTRIES_TABLE}.leagueCycle, VALUES(leagueCycle)),
      updatedAt = NOW(6)`;

  private static readonly UPSERT_COLUMNS_SQL =
    '(id, playerId, uid, score, leaguePoint, leagueCycle, countryCode, scoreUpdatedAt, rankedAt, createdAt, updatedAt)';
  private static readonly UPSERT_ROW_SQL = '(?, ?, ?, ?, ?, ?, ?, NOW(6), NOW(6), NOW(6), NOW(6))';

  private static readonly RESET_BATCH = 300;
  private static readonly RESET_MAX_ROWS = 200_000;

  constructor(
    @InjectDataSource() private readonly dataSource: DataSource,
    @Inject(LEADERBOARD_OPTIONS) private readonly options: ResolvedLeaderboardOptions,
  ) {}

  async withMaster<T>(fn: (runner: QueryRunner) => Promise<T>): Promise<T> {
    const runner = this.dataSource.createQueryRunner('master');
    try {
      await runner.connect();
      return await fn(runner);
    } finally {
      await runner.release();
    }
  }

  /** Thống kê (histogram, danh sách quốc gia) đọc từ replica nếu bật; lỗi thì về master. */
  async withRead<T>(fn: (runner: QueryRunner) => Promise<T>): Promise<T> {
    if (!this.options.readReplica) return this.withMaster(fn);
    let runner: QueryRunner | null = null;
    try {
      runner = this.dataSource.createQueryRunner('slave');
      await runner.connect();
      return await fn(runner);
    } catch (error) {
      this.logger.warn(`đọc replica lỗi, chuyển về master: ${(error as Error).message}`);
      return this.withMaster(fn);
    } finally {
      if (runner) await runner.release();
    }
  }

  // ---- ghi điểm ----

  /**
   * Upsert cả lô bằng MỘT câu lệnh; uid trong blocklist bị lọc bỏ. Trả số dòng đã ghi.
   * Gộp nhiều lần gửi của cùng một người thành một vẫn cho đúng kết quả vì mệnh đề
   * ODKU chỉ tăng.
   */
  async upsertBatch(rows: ScoreWrite[]): Promise<number> {
    const valid = rows.filter(
      (r) => r.playerId && r.uid && Number.isFinite(r.score) && r.score >= 0,
    );
    if (valid.length === 0) return 0;

    return this.withMaster(async (runner) => {
      await applyLockWaitTimeout(runner);
      const blocked = await this.blockedAmong(runner, valid.map((r) => r.uid));
      const allowed = valid.filter((r) => !blocked.has(r.uid));
      if (allowed.length === 0) return 0;

      const params: unknown[] = [];
      const tuples = allowed
        .map((r) => {
          params.push(
            randomUUID(),
            r.playerId,
            r.uid,
            Math.floor(r.score),
            toPoint(r.secondaryScore),
            toSeason(r.season),
            r.countryCode ?? null,
          );
          return LeaderboardStore.UPSERT_ROW_SQL;
        })
        .join(', ');

      await runner.query(
        `INSERT INTO ${ENTRIES_TABLE} ${LeaderboardStore.UPSERT_COLUMNS_SQL}
         VALUES ${tuples}
         ON DUPLICATE KEY UPDATE ${LeaderboardStore.UPSERT_ON_DUPLICATE_SQL}`,
        params,
      );
      return allowed.length;
    });
  }

  async findByPlayerId(playerId: string): Promise<(EntryRow & { scoreUpdatedAt: Date }) | null> {
    const rows = await this.withMaster((r) =>
      r.query(
        `SELECT ${ROW_COLUMNS_SQL}, scoreUpdatedAt FROM ${ENTRIES_TABLE} WHERE playerId = ? LIMIT 1`,
        [playerId],
      ),
    );
    return rows[0] ? { ...this.toRow(rows[0]), scoreUpdatedAt: rows[0].scoreUpdatedAt } : null;
  }

  /** Đọc từ master: client hay tra hạng ngay sau khi gửi điểm. */
  async findByUid(uid: string): Promise<EntryRow | null> {
    const rows = await this.withMaster((r) =>
      r.query(`SELECT ${ROW_COLUMNS_SQL} FROM ${ENTRIES_TABLE} WHERE uid = ? LIMIT 1`, [uid]),
    );
    return rows[0] ? this.toRow(rows[0]) : null;
  }

  async updateCountry(playerId: string, countryCode: string | null): Promise<void> {
    await this.withMaster((r) =>
      r.query(`UPDATE ${ENTRIES_TABLE} SET countryCode = ? WHERE playerId = ?`, [
        countryCode,
        playerId,
      ]),
    );
  }

  async deleteByPlayerId(playerId: string): Promise<void> {
    await this.withMaster((r) =>
      r.query(`DELETE FROM ${ENTRIES_TABLE} WHERE playerId = ?`, [playerId]),
    );
  }

  /** Đưa điểm của uid bị cấm về 0 (dòng vẫn còn, chỉ rơi xuống đáy bảng). */
  async zeroScoresOfUid(uid: string): Promise<void> {
    await this.withMaster((r) =>
      r.query(
        `UPDATE ${ENTRIES_TABLE}
            SET score = 0, leaguePoint = 0, scoreUpdatedAt = NOW(6), rankedAt = NOW(6), updatedAt = NOW(6)
          WHERE uid = ?`,
        [uid],
      ),
    );
  }

  // ---- đọc bảng ----

  /** Top `limit` của một board theo đúng thứ tự xếp hạng. Không JOIN — profile nạp riêng. */
  async loadTop(board: string, limit: number): Promise<EntryRow[]> {
    const world = board === WORLD_BOARD;
    const rows = await this.withMaster((r) =>
      r.query(
        `SELECT ${ROW_COLUMNS_SQL} FROM ${ENTRIES_TABLE}
          ${world ? '' : 'WHERE countryCode = ?'}
          ORDER BY ${ORDER_BY_SQL}
          LIMIT ${Math.floor(limit)}`,
        world ? [] : [board],
      ),
    );
    return rows.map((row: any) => this.toRow(row));
  }

  async distinctCountries(): Promise<string[]> {
    const rows = await this.withRead((r) =>
      r.query(`SELECT DISTINCT countryCode AS cc FROM ${ENTRIES_TABLE} WHERE countryCode IS NOT NULL`),
    );
    return rows.map((row: any) => String(row.cc)).filter(Boolean);
  }

  /**
   * Số dòng theo (quốc gia, điểm) cho MỌI board trong MỘT truy vấn — đi đúng index
   * (countryCode, score, …) nên chỉ một lượt quét. Điểm là Level nên kết quả chỉ vài
   * chục nghìn dòng dù bảng có hàng triệu người.
   */
  async scoreCounts(): Promise<Array<{ countryCode: string | null; score: number; count: number }>> {
    const rows = await this.withRead((r) =>
      r.query(
        `SELECT countryCode AS cc, score, COUNT(*) AS cnt FROM ${ENTRIES_TABLE} GROUP BY countryCode, score`,
      ),
    );
    return rows.map((row: any) => ({
      countryCode: row.cc ?? null,
      score: Number(row.score),
      count: Number(row.cnt),
    }));
  }

  /**
   * Điều kiện "đứng trên `entry`" — phải khớp từng chữ với ORDER_BY_SQL.
   * `sameScoreOnly`: chỉ xét nhóm cùng điểm (phần "điểm cao hơn" đã có histogram lo).
   */
  private betterThanSql(sameScoreOnly: boolean): string {
    const tie = `(leaguePoint > ? OR (leaguePoint = ? AND (rankedAt < ? OR (rankedAt = ? AND id < ?))))`;
    return sameScoreOnly ? `score = ? AND ${tie}` : `(score > ? OR (score = ? AND ${tie}))`;
  }

  private tieParams(entry: EntryRow): unknown[] {
    return [entry.secondaryScore, entry.secondaryScore, entry.rankedAt, entry.rankedAt, entry.id];
  }

  /** Số người cùng điểm mà đứng trên `entry` — COUNT gói trong đúng một bucket điểm. */
  async countSameScoreBetter(entry: EntryRow, board: string): Promise<number> {
    const world = board === WORLD_BOARD;
    const rows = await this.withMaster((r) =>
      r.query(
        `SELECT COUNT(*) AS c FROM ${ENTRIES_TABLE}
          WHERE ${this.betterThanSql(true)} ${world ? '' : 'AND countryCode = ?'}`,
        [entry.score, ...this.tieParams(entry), ...(world ? [] : [board])],
      ),
    );
    return Number(rows[0]?.c ?? 0);
  }

  /** Đường lùi khi chưa có histogram: đếm thẳng. Chậm trên bảng lớn nhưng không bao giờ sai. */
  async countRankDirect(entry: EntryRow, board: string): Promise<{ total: number; better: number }> {
    const world = board === WORLD_BOARD;
    return this.withMaster(async (r) => {
      const [totalRows, betterRows] = await Promise.all([
        r.query(
          `SELECT COUNT(*) AS c FROM ${ENTRIES_TABLE} ${world ? '' : 'WHERE countryCode = ?'}`,
          world ? [] : [board],
        ),
        r.query(
          `SELECT COUNT(*) AS c FROM ${ENTRIES_TABLE}
            WHERE ${this.betterThanSql(false)} ${world ? '' : 'AND countryCode = ?'}`,
          [entry.score, entry.score, ...this.tieParams(entry), ...(world ? [] : [board])],
        ),
      ]);
      return { total: Number(totalRows[0]?.c ?? 0), better: Number(betterRows[0]?.c ?? 0) };
    });
  }

  // ---- mùa của điểm phụ ----

  /**
   * Đưa điểm phụ về 0 cho mọi dòng còn giữ điểm của mùa đã qua.
   *
   * Người chơi còn hoạt động tự được reset ở lần ghi kế tiếp (nhánh "mùa mới hơn" của
   * ODKU); việc ở đây là dọn người không mở game nữa, nếu không họ nằm lì trên đầu bảng.
   *
   * Hai bước, đọc rồi mới ghi: đọc id bằng quét index phủ (không khoá gì), rồi ghi theo lô
   * nhỏ tra khoá chính với điều kiện `leagueCycle < ?` — dòng vừa được ghi sang mùa mới
   * xen giữa hai bước sẽ tự bị bỏ qua. Chạy lại bao nhiêu lần cũng cho cùng kết quả.
   */
  async resetStaleSecondary(season: number): Promise<number> {
    if (!Number.isFinite(season) || season <= FIRST_SEASON) return 0;
    return this.withMaster(async (runner) => {
      const select = (hint: string) =>
        runner.query(
          `SELECT /*+ MAX_EXECUTION_TIME(60000) */ id FROM ${ENTRIES_TABLE} ${hint}
            WHERE leaguePoint > 0 LIMIT ${LeaderboardStore.RESET_MAX_ROWS}`,
        ) as Promise<Array<{ id: string }>>;

      let rows: Array<{ id: string }>;
      try {
        rows = await select(`FORCE INDEX (\`${WORLD_ORDER_INDEX}\`)`);
      } catch (error) {
        if (!isMissingIndexError(error)) throw error;
        rows = await select('');
      }
      if (rows.length === 0) return 0;

      const ids = rows.map((r) => r.id).sort();
      await applyLockWaitTimeout(runner);
      let reset = 0;
      for (let i = 0; i < ids.length; i += LeaderboardStore.RESET_BATCH) {
        const batch = ids.slice(i, i + LeaderboardStore.RESET_BATCH);
        const result = await runWithLockRetry(() =>
          runner.query(
            `UPDATE ${ENTRIES_TABLE}
                SET leaguePoint = 0, leagueCycle = ?, rankedAt = scoreUpdatedAt, updatedAt = NOW(6)
              WHERE id IN (${batch.map(() => '?').join(',')}) AND leagueCycle < ? AND leaguePoint > 0`,
            [season, ...batch, season],
          ),
        );
        reset += Number(result?.affectedRows ?? 0);
        await yieldToEventLoop();
      }
      return reset;
    });
  }

  // ---- blocklist ----

  private async blockedAmong(runner: QueryRunner, uids: string[]): Promise<Set<string>> {
    const unique = [...new Set(uids)];
    if (unique.length === 0) return new Set();
    const rows = await runner.query(
      `SELECT uid FROM ${BLOCKED_UIDS_TABLE} WHERE uid IN (${unique.map(() => '?').join(',')})`,
      unique,
    );
    return new Set(rows.map((r: any) => String(r.uid)));
  }

  async loadBlockedUids(): Promise<Set<string>> {
    const rows = await this.withMaster((r) => r.query(`SELECT uid FROM ${BLOCKED_UIDS_TABLE}`));
    return new Set(rows.map((r: any) => String(r.uid)));
  }

  async insertBlockedUid(uid: string, reason: string | null): Promise<void> {
    await this.withMaster((r) =>
      r.query(
        `INSERT INTO ${BLOCKED_UIDS_TABLE} (id, uid, reason, createdAt, updatedAt)
         VALUES (?, ?, ?, NOW(6), NOW(6))
         ON DUPLICATE KEY UPDATE reason = VALUES(reason), updatedAt = NOW(6)`,
        [randomUUID(), uid, reason],
      ),
    );
  }

  async deleteBlockedUid(uid: string): Promise<void> {
    await this.withMaster((r) => r.query(`DELETE FROM ${BLOCKED_UIDS_TABLE} WHERE uid = ?`, [uid]));
  }

  private toRow(row: any): EntryRow {
    return {
      id: String(row.id),
      playerId: String(row.playerId),
      uid: String(row.uid),
      score: Number(row.score),
      secondaryScore: Number(row.secondaryScore ?? 0),
      season: toSeason(row.season),
      countryCode: row.countryCode ?? null,
      rankedAt: String(row.rankedAt),
    };
  }
}
