import type { MigrationInterface, QueryRunner } from 'typeorm';
import { BLOCKED_UIDS_TABLE, ENTRIES_TABLE } from '../leaderboard.constants';

/** Hai index phủ trọn thứ tự xếp hạng — `GET /top` và phép đếm hạng đều đi trên chúng. */
export const WORLD_ORDER_INDEX = 'IDX_lb_entries_score_leaguePoint_rankedAt';
export const COUNTRY_ORDER_INDEX = 'IDX_lb_entries_country_score_leaguePoint_rankedAt';

/**
 * Tạo hoặc NÂNG CẤP schema bảng xếp hạng. Chạy lại bao nhiêu lần cũng được.
 *
 * - Chưa có bảng: tạo đầy đủ.
 * - Có bảng từ template game-backend cũ: chỉ thêm cột / index còn thiếu, giữ nguyên dữ
 *   liệu, không đụng khoá ngoại hay index cũ đang có.
 *
 * Nâng cấp từ bản rất cũ (chưa có `rankedAt`) sẽ UPDATE toàn bảng một lần — bảng hàng
 * triệu dòng thì chạy lúc thấp điểm.
 *
 * `down()` cố ý không xoá gì: bảng có thể đã chứa dữ liệu từ trước khi cài package.
 */
export class LeaderboardSchema1790000000000 implements MigrationInterface {
  name = 'LeaderboardSchema1790000000000';

  public async up(queryRunner: QueryRunner): Promise<void> {
    await this.ensureEntries(queryRunner);
    await this.ensureBlockedUids(queryRunner);
  }

  public async down(): Promise<void> {
    // Không xoá gì — xem ghi chú của class.
  }

  private async ensureEntries(q: QueryRunner): Promise<void> {
    if (!(await q.hasTable(ENTRIES_TABLE))) {
      await q.query(`
        CREATE TABLE \`${ENTRIES_TABLE}\` (
          \`id\` varchar(36) NOT NULL,
          \`playerId\` varchar(36) NOT NULL,
          \`uid\` varchar(10) NOT NULL,
          \`score\` int NOT NULL,
          \`leaguePoint\` int NOT NULL DEFAULT 0,
          \`leagueCycle\` int NOT NULL DEFAULT 1,
          \`countryCode\` varchar(2) NULL,
          \`scoreUpdatedAt\` datetime(6) NOT NULL,
          \`rankedAt\` datetime(6) NOT NULL,
          \`createdAt\` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
          \`updatedAt\` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
          PRIMARY KEY (\`id\`),
          UNIQUE INDEX \`IDX_leaderboard_entries_playerId\` (\`playerId\`),
          INDEX \`IDX_leaderboard_entries_uid\` (\`uid\`),
          INDEX \`${WORLD_ORDER_INDEX}\` (\`score\` DESC, \`leaguePoint\` DESC, \`rankedAt\` ASC),
          INDEX \`${COUNTRY_ORDER_INDEX}\` (\`countryCode\` ASC, \`score\` DESC, \`leaguePoint\` DESC, \`rankedAt\` ASC)
        ) ENGINE=InnoDB`);
      return;
    }

    const table = await q.getTable(ENTRIES_TABLE);
    const hasColumn = (name: string) => !!table?.findColumnByName(name);

    if (!hasColumn('leaguePoint')) {
      await q.query(
        `ALTER TABLE \`${ENTRIES_TABLE}\` ADD COLUMN \`leaguePoint\` int NOT NULL DEFAULT 0`,
      );
    }
    if (!hasColumn('leagueCycle')) {
      await q.query(
        `ALTER TABLE \`${ENTRIES_TABLE}\` ADD COLUMN \`leagueCycle\` int NOT NULL DEFAULT 1`,
      );
    }
    if (!hasColumn('rankedAt')) {
      // Thêm NULL trước, điền bằng scoreUpdatedAt (đúng nghĩa khi chưa có điểm phụ),
      // rồi mới siết NOT NULL — thêm thẳng NOT NULL thì mọi dòng cũ nhận giá trị rác.
      await q.query(`ALTER TABLE \`${ENTRIES_TABLE}\` ADD COLUMN \`rankedAt\` datetime(6) NULL`);
      await q.query(
        `UPDATE \`${ENTRIES_TABLE}\` SET \`rankedAt\` = \`scoreUpdatedAt\` WHERE \`rankedAt\` IS NULL`,
      );
      await q.query(
        `ALTER TABLE \`${ENTRIES_TABLE}\` MODIFY \`rankedAt\` datetime(6) NOT NULL`,
      );
    }

    const indexes: Array<{ name: string; column: string; seq: number; nonUnique: number }> = (
      await q.query(
        `SELECT INDEX_NAME AS name, COLUMN_NAME AS \`column\`, SEQ_IN_INDEX AS seq, NON_UNIQUE AS nonUnique
           FROM information_schema.STATISTICS
          WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = ?`,
        [ENTRIES_TABLE],
      )
    ).map((r: any) => ({
      name: String(r.name),
      column: String(r.column),
      seq: Number(r.seq),
      nonUnique: Number(r.nonUnique),
    }));
    const names = new Set(indexes.map((i) => i.name));
    const leading = (column: string, unique: boolean) =>
      indexes.some((i) => i.seq === 1 && i.column === column && (!unique || i.nonUnique === 0));

    // ON DUPLICATE KEY UPDATE dựa vào unique trên playerId — thiếu nó mỗi lần ghi sẽ
    // thêm một dòng mới. Có dữ liệu trùng thì câu này hỏng; phải dọn tay rồi chạy lại.
    if (!leading('playerId', true)) {
      await q.query(
        `CREATE UNIQUE INDEX \`IDX_leaderboard_entries_playerId\` ON \`${ENTRIES_TABLE}\` (\`playerId\`)`,
      );
    }
    if (!leading('uid', false)) {
      await q.query(`CREATE INDEX \`IDX_leaderboard_entries_uid\` ON \`${ENTRIES_TABLE}\` (\`uid\`)`);
    }
    if (!names.has(WORLD_ORDER_INDEX)) {
      await q.query(
        `CREATE INDEX \`${WORLD_ORDER_INDEX}\` ON \`${ENTRIES_TABLE}\` (\`score\` DESC, \`leaguePoint\` DESC, \`rankedAt\` ASC)`,
      );
    }
    if (!names.has(COUNTRY_ORDER_INDEX)) {
      await q.query(
        `CREATE INDEX \`${COUNTRY_ORDER_INDEX}\` ON \`${ENTRIES_TABLE}\` (\`countryCode\` ASC, \`score\` DESC, \`leaguePoint\` DESC, \`rankedAt\` ASC)`,
      );
    }
  }

  private async ensureBlockedUids(q: QueryRunner): Promise<void> {
    if (await q.hasTable(BLOCKED_UIDS_TABLE)) return;
    await q.query(`
      CREATE TABLE \`${BLOCKED_UIDS_TABLE}\` (
        \`id\` varchar(36) NOT NULL,
        \`uid\` varchar(10) NOT NULL,
        \`reason\` text NULL,
        \`createdAt\` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
        \`updatedAt\` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
        PRIMARY KEY (\`id\`),
        UNIQUE INDEX \`IDX_leaderboard_blocked_uids_uid\` (\`uid\`)
      ) ENGINE=InnoDB`);
  }
}

/** Thêm vào `migrations` của DataSource host. */
export const LEADERBOARD_MIGRATIONS = [LeaderboardSchema1790000000000];
