import { ForbiddenException } from '@nestjs/common';
import { randomUUID } from 'crypto';
import request from 'supertest';
import { DataSource } from 'typeorm';
import { LEADERBOARD_MIGRATIONS, LeaderboardSchema1790000000000, LeaderboardService } from '../../src';
import { resolveLeaderboardOptions } from '../../src/leaderboard.options';
import { COUNTRY_ORDER_INDEX, WORLD_ORDER_INDEX } from '../../src/migrations/LeaderboardSchema1790000000000';
import { BoardService } from '../../src/services/board.service';
import { LeaderboardStore } from '../../src/services/leaderboard.store';
import { LeaderboardMaintenanceService } from '../../src/services/maintenance.service';
import { RankService } from '../../src/services/rank.service';
import { ScoreFlushService } from '../../src/services/score-flush.service';
import { ScoreQueueService } from '../../src/services/score-queue.service';
import { isPermanentRowError } from '../../src/util/mysql';
import {
  closeTestApp,
  createScratchDatabase,
  createTestApp,
  dropDatabase,
  HeaderAuthGuard,
  PLAYERS_DDL,
  TEST_REDIS,
  TestApp,
  TestSeasons,
  uidOf,
} from './helpers';

const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

describe('abi-leaderboard (MySQL + Redis thật)', () => {
  let t: TestApp;
  let ds: DataSource;
  let svc: LeaderboardService;
  let store: LeaderboardStore;
  let queue: ScoreQueueService;
  let flush: ScoreFlushService;
  let ranks: RankService;
  let boards: BoardService;

  beforeAll(async () => {
    t = await createTestApp();
    ds = t.dataSource;
    svc = t.moduleRef.get(LeaderboardService);
    store = t.moduleRef.get(LeaderboardStore, { strict: false });
    queue = t.moduleRef.get(ScoreQueueService, { strict: false });
    flush = t.moduleRef.get(ScoreFlushService, { strict: false });
    ranks = t.moduleRef.get(RankService, { strict: false });
    boards = t.moduleRef.get(BoardService, { strict: false });
  });

  afterAll(async () => {
    await closeTestApp(t);
  });

  beforeEach(async () => {
    TestSeasons.season = 1;
    await ds.query('DELETE FROM leaderboard_entries');
    await ds.query('DELETE FROM leaderboard_blocked_uids');
    await ds.query('DELETE FROM players');
    await t.moduleRef.get(TEST_REDIS).flushDb();
  });

  describe('migration', () => {
    it('DB trống: tạo đủ bảng và index, index thứ tự có đúng chiều DESC', async () => {
      const idx = await ds.query(
        `SELECT INDEX_NAME AS name, COLUMN_NAME AS col, SEQ_IN_INDEX AS seq, COLLATION AS dir, NON_UNIQUE AS nu
           FROM information_schema.STATISTICS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'leaderboard_entries'`,
      );
      const of = (name: string) =>
        idx.filter((r: any) => r.name === name).sort((a: any, b: any) => a.seq - b.seq).map((r: any) => `${r.col}:${r.dir}`);
      expect(of(WORLD_ORDER_INDEX)).toEqual(['score:D', 'leaguePoint:D', 'rankedAt:A']);
      expect(of(COUNTRY_ORDER_INDEX)).toEqual(['countryCode:A', 'score:D', 'leaguePoint:D', 'rankedAt:A']);
      expect(idx.some((r: any) => r.col === 'playerId' && Number(r.nu) === 0)).toBe(true);
      expect(await ds.query(`SHOW TABLES LIKE 'leaderboard_blocked_uids'`)).toHaveLength(1);
    });

    it('chạy lại không lỗi, không đổi gì', async () => {
      const runner = ds.createQueryRunner();
      await new LeaderboardSchema1790000000000().up(runner);
      await new LeaderboardSchema1790000000000().up(runner);
      await runner.release();
    });

    it('nâng cấp bảng của template cũ: giữ dữ liệu, thêm cột + index, khoá ngoại cũ vẫn còn', async () => {
      const url = await createScratchDatabase('lblegacy');
      const legacy = new DataSource({ type: 'mysql', url, migrations: LEADERBOARD_MIGRATIONS });
      await legacy.initialize();
      try {
        await legacy.query(PLAYERS_DDL);
        await legacy.query(`
          CREATE TABLE leaderboard_entries (
            id varchar(36) NOT NULL, playerId varchar(36) NOT NULL, uid varchar(10) NOT NULL,
            score int NOT NULL, countryCode varchar(2) NULL, scoreUpdatedAt datetime(6) NOT NULL,
            createdAt datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
            updatedAt datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
            PRIMARY KEY (id), UNIQUE INDEX IDX_leaderboard_entries_playerId (playerId),
            INDEX IDX_leaderboard_entries_uid (uid), INDEX IDX_leaderboard_entries_score (score),
            CONSTRAINT FK_leaderboard_entries_playerId FOREIGN KEY (playerId) REFERENCES players(id) ON DELETE CASCADE
          ) ENGINE=InnoDB`);
        const pid = randomUUID();
        await legacy.query(`INSERT INTO players (id, userId, uid, countryCode) VALUES (?, ?, ?, 'vn')`, [pid, randomUUID(), uidOf(1)]);
        await legacy.query(
          `INSERT INTO leaderboard_entries (id, playerId, uid, score, countryCode, scoreUpdatedAt)
           VALUES (?, ?, ?, 7, 'vn', '2026-01-02 03:04:05.123456')`,
          [randomUUID(), pid, uidOf(1)],
        );

        await legacy.runMigrations();

        const [row] = await legacy.query(
          `SELECT score, leaguePoint, leagueCycle, DATE_FORMAT(rankedAt, '%Y-%m-%d %H:%i:%s.%f') AS rankedAt FROM leaderboard_entries`,
        );
        expect(row).toEqual({ score: 7, leaguePoint: 0, leagueCycle: 1, rankedAt: '2026-01-02 03:04:05.123456' });
        const names = (await legacy.query(
          `SELECT DISTINCT INDEX_NAME AS n FROM information_schema.STATISTICS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'leaderboard_entries'`,
        )).map((r: any) => r.n);
        expect(names).toEqual(expect.arrayContaining([WORLD_ORDER_INDEX, COUNTRY_ORDER_INDEX, 'IDX_leaderboard_entries_score']));
        const fks = await legacy.query(
          `SELECT CONSTRAINT_NAME FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE()`,
        );
        expect(fks).toHaveLength(1);

        // Đúng ca của Colony Flow 14/10: ghi điểm cho player đã bị xoá → khoá ngoại 1452.
        // Phải được nhận diện là lỗi dữ liệu để worker bỏ dòng, không requeue mãi.
        const legacyStore = new LeaderboardStore(
          legacy,
          resolveLeaderboardOptions({ redisToken: 'x', authGuard: HeaderAuthGuard }),
        );
        let caught: unknown;
        try {
          await legacyStore.upsertBatch([
            { playerId: randomUUID(), uid: uidOf(2), score: 3, countryCode: null, secondaryScore: 0, season: 1 },
          ]);
        } catch (error) {
          caught = error;
        }
        expect(isPermanentRowError(caught)).toBe(true);

        const runner = legacy.createQueryRunner();
        await new LeaderboardSchema1790000000000().up(runner);
        await runner.release();
      } finally {
        await legacy.destroy();
        await dropDatabase(url);
      }
    });
  });

  describe('câu upsert', () => {
    const base = () => ({ playerId: randomUUID(), uid: uidOf(70_000), countryCode: 'us', secondaryScore: 0, season: 1 });

    it('điểm chỉ tăng; scoreUpdatedAt chỉ đổi khi điểm tăng', async () => {
      const p = base();
      await store.upsertBatch([{ ...p, score: 10 }]);
      const a = await store.findByPlayerId(p.playerId);
      await sleep(5);
      await store.upsertBatch([{ ...p, score: 5 }]);
      const b = await store.findByPlayerId(p.playerId);
      expect(b!.score).toBe(10);
      expect(b!.scoreUpdatedAt.getTime()).toBe(a!.scoreUpdatedAt.getTime());
      await sleep(5);
      await store.upsertBatch([{ ...p, score: 12 }]);
      const c = await store.findByPlayerId(p.playerId);
      expect(c!.score).toBe(12);
      expect(c!.scoreUpdatedAt.getTime()).toBeGreaterThan(a!.scoreUpdatedAt.getTime());
    });

    it('điểm phụ: cùng mùa lấy max, mùa mới ghi đè, mùa cũ tới muộn bị bỏ', async () => {
      const p = { ...base(), score: 20 };
      await store.upsertBatch([{ ...p, secondaryScore: 3, season: 1 }]);
      await store.upsertBatch([{ ...p, secondaryScore: 1, season: 1 }]);
      expect((await store.findByPlayerId(p.playerId))!.secondaryScore).toBe(3);

      await store.upsertBatch([{ ...p, secondaryScore: 1, season: 2 }]);
      let row = await store.findByPlayerId(p.playerId);
      expect([row!.secondaryScore, row!.season]).toEqual([1, 2]);

      await store.upsertBatch([{ ...p, secondaryScore: 9, season: 1 }]);
      row = await store.findByPlayerId(p.playerId);
      expect([row!.secondaryScore, row!.season]).toEqual([1, 2]);
    });

    it('reset điểm phụ về 0 thì rankedAt lùi về lúc đạt điểm hiện tại', async () => {
      const p = { ...base(), score: 30 };
      await store.upsertBatch([{ ...p, secondaryScore: 4, season: 1 }]);
      await sleep(5);
      await store.upsertBatch([{ ...p, secondaryScore: 0, season: 2 }]);
      const [r] = await ds.query(
        'SELECT rankedAt = scoreUpdatedAt AS same, leaguePoint FROM leaderboard_entries WHERE playerId = ?',
        [p.playerId],
      );
      expect(Number(r.same)).toBe(1);
      expect(r.leaguePoint).toBe(0);
    });
  });

  describe('hàng đợi + worker flush', () => {
    it('300 player × 5 lần gửi lộn xộn → DB giữ đúng điểm cao nhất, hàng đợi rỗng', async () => {
      const players = Array.from({ length: 300 }, (_, i) => ({
        playerId: randomUUID(),
        uid: uidOf(50_000 + i),
        countryCode: i % 2 ? 'vn' : null,
      }));
      const best = new Map<string, number>();
      for (let round = 0; round < 5; round += 1) {
        for (const p of players) {
          const s = Math.floor(Math.random() * 500);
          await svc.submitForPlayer(p, s);
          best.set(p.playerId, Math.max(best.get(p.playerId) ?? -1, s));
        }
      }
      await flush.flushNow();
      const rows: Array<{ playerId: string; score: number }> = await ds.query(
        'SELECT playerId, score FROM leaderboard_entries',
      );
      expect(rows).toHaveLength(300);
      for (const r of rows) expect(r.score).toBe(best.get(r.playerId));
      expect(await queue.size()).toBe(0);
    });

    it('dòng hỏng vì dữ liệu bị bỏ, phần còn lại vẫn ghi, không kẹt trong in-flight', async () => {
      const good = Array.from({ length: 5 }, (_, i) => ({
        playerId: randomUUID(), uid: uidOf(60_000 + i), score: 10 + i, countryCode: 'jp', secondaryScore: 0, season: 1,
      }));
      const bad = { playerId: randomUUID(), uid: uidOf(60_100), score: 99, countryCode: 'xyz', secondaryScore: 0, season: 1 };
      for (const w of [...good, bad]) await queue.enqueue(w);

      await flush.flushNow();

      const rows = await ds.query('SELECT playerId FROM leaderboard_entries');
      expect(rows.map((r: any) => r.playerId).sort()).toEqual(good.map((g) => g.playerId).sort());
      expect(await queue.size()).toBe(0);
      const redis = t.moduleRef.get(TEST_REDIS);
      const ids: string[] = await redis.sMembers('lb:v1:score-inflight-ids');
      for (const id of ids) expect(await redis.hLen(`lb:v1:score-inflight:${id}`)).toBe(0);
    });

    it('uid bị cấm: submit bị từ chối, điểm đã xếp hàng cũng bị lọc lúc flush', async () => {
      const p = { playerId: randomUUID(), uid: uidOf(91_000), countryCode: null };
      await svc.blockUid(p.uid, 'test');
      await expect(svc.submitForPlayer(p, 50)).rejects.toBeInstanceOf(ForbiddenException);
      await queue.enqueue({ ...p, score: 50, secondaryScore: 0, season: 1 });
      await flush.flushNow();
      expect(await ds.query('SELECT * FROM leaderboard_entries WHERE uid = ?', [p.uid])).toHaveLength(0);
    });

    it('syncScore (đường lưu profile) không ném: uid bị cấm / điểm hỏng → false, điểm lẻ làm tròn xuống', async () => {
      const blocked = { playerId: randomUUID(), uid: uidOf(92_000), countryCode: null };
      await svc.blockUid(blocked.uid);
      await expect(svc.syncScore(blocked, 40)).resolves.toBe(false);

      const p = { playerId: randomUUID(), uid: uidOf(92_001), countryCode: 'vn' };
      await expect(svc.syncScore(p, -1)).resolves.toBe(false);
      await expect(svc.syncScore(p, Number.NaN)).resolves.toBe(false);
      await expect(svc.syncScore(p, 12.9)).resolves.toBe(true);
      await flush.flushNow();
      const rows = await ds.query('SELECT uid, score FROM leaderboard_entries WHERE uid IN (?, ?)', [blocked.uid, p.uid]);
      expect(rows).toEqual([{ uid: p.uid, score: 12 }]);
    });

    it('syncScore(userId) lấy quốc gia từ bảng player, đổi quốc gia không bị cache kéo về', async () => {
      const seed = async (cc: string | null) => {
        const ids = { userId: randomUUID(), playerId: randomUUID() };
        await ds.query('INSERT INTO players (id, userId, uid, countryCode, info) VALUES (?, ?, ?, ?, ?)', [
          ids.playerId, ids.userId, uidOf(93_000 + Math.floor(Math.random() * 999)), cc, '{}',
        ]);
        return ids;
      };
      const changeCountry = async (ids: { userId: string; playerId: string }, cc: string) => {
        await ds.query('UPDATE players SET countryCode = ? WHERE id = ?', [cc, ids.playerId]);
        await svc.syncCountry(ids.playerId, cc, ids.userId);
      };
      const rowOf = async (playerId: string) =>
        (await ds.query('SELECT score, countryCode FROM leaderboard_entries WHERE playerId = ?', [playerId]))[0];

      // Người chơi mới: chọn quốc gia khi chưa có dòng BXH, rồi mới lưu Level.
      const fresh = await seed(null);
      await changeCountry(fresh, 'vn');
      await expect(svc.syncScore(fresh.userId, 3)).resolves.toBe(true);

      // Đổi từ us sang vn khi cache của package đang giữ "us".
      const mover = await seed('us');
      await svc.syncScore(mover.userId, 3);
      await flush.flushNow();
      await changeCountry(mover, 'vn');
      await svc.syncScore(mover.userId, 4);
      await flush.flushNow();

      expect(await rowOf(fresh.playerId)).toEqual({ score: 3, countryCode: 'vn' });
      expect(await rowOf(mover.playerId)).toEqual({ score: 4, countryCode: 'vn' });
      await expect(svc.syncScore(randomUUID(), 5)).resolves.toBe(false);
    });
  });

  describe('hạng khớp vị trí trong bảng', () => {
    const N = 4000;

    beforeEach(async () => {
      // Điểm 1..80, điểm phụ 0..3, rankedAt chỉ 6 giá trị → rất nhiều dòng trùng HOÀN TOÀN.
      const countries = ['vn', 'us', 'jp', null];
      const times = Array.from({ length: 6 }, (_, i) => `2026-10-0${i + 1} 10:00:00.000000`);
      for (let start = 0; start < N; start += 500) {
        const params: unknown[] = [];
        const tuples: string[] = [];
        for (let i = start; i < Math.min(N, start + 500); i += 1) {
          const at = times[Math.floor(Math.random() * times.length)];
          params.push(randomUUID(), randomUUID(), uidOf(i), 1 + Math.floor(Math.random() * 80),
            Math.floor(Math.random() * 4), countries[i % 4], at, at);
          tuples.push('(?, ?, ?, ?, ?, 1, ?, ?, ?)');
        }
        await ds.query(
          `INSERT INTO leaderboard_entries (id, playerId, uid, score, leaguePoint, leagueCycle, countryCode, scoreUpdatedAt, rankedAt)
           VALUES ${tuples.join(', ')}`,
          params,
        );
      }
    });

    async function expectedPositions(): Promise<Map<string, Map<string, number>>> {
      const rows: Array<{ uid: string; countryCode: string | null }> = await ds.query(
        'SELECT uid, countryCode FROM leaderboard_entries ORDER BY score DESC, leaguePoint DESC, rankedAt ASC, id ASC',
      );
      const out = new Map<string, Map<string, number>>([['ww', new Map()]]);
      rows.forEach((r, i) => {
        out.get('ww')!.set(r.uid, i + 1);
        if (!r.countryCode) return;
        if (!out.has(r.countryCode)) out.set(r.countryCode, new Map());
        const board = out.get(r.countryCode)!;
        board.set(r.uid, board.size + 1);
      });
      return out;
    }

    function sampleUids(n: number): string[] {
      return Array.from({ length: n }, () => uidOf(Math.floor(Math.random() * N)));
    }

    it('đường histogram: World và quốc gia khớp từng vị trí', async () => {
      const expected = await expectedPositions();
      expect(await ranks.refreshHistograms()).toBe(4);
      for (const uid of sampleUids(300)) {
        const world = await ranks.compute(uid, 'ww');
        expect(world!.rank).toBe(expected.get('ww')!.get(uid));
        expect(world!.total).toBe(N);
        const cc = world!.countryCode;
        if (cc) {
          expect((await ranks.compute(uid, cc))!.rank).toBe(expected.get(cc)!.get(uid));
          expect(await ranks.compute(uid, cc === 'vn' ? 'us' : 'vn')).toBeNull();
        }
      }
    });

    it('đường đếm thẳng (chưa có histogram) cho cùng kết quả', async () => {
      const expected = await expectedPositions();
      for (const uid of sampleUids(100)) {
        const world = await ranks.compute(uid, 'ww');
        expect(world!.rank).toBe(expected.get('ww')!.get(uid));
      }
    });

    it('vị trí trong bảng top khớp thứ tự chuẩn', async () => {
      const expected = await expectedPositions();
      const world = await boards.buildBoard('ww');
      expect(world).toHaveLength(1000);
      world.forEach((e) => expect(e.rank).toBe(expected.get('ww')!.get(e.uid)));
      const vn = await boards.buildBoard('vn');
      vn.forEach((e) => expect(e.rank).toBe(expected.get('vn')!.get(e.uid)));
    });

    it('thứ tự có chốt id vẫn đi trên index, không sắp xếp lại cả bảng', async () => {
      const world = await ds.query(
        `EXPLAIN SELECT id FROM leaderboard_entries FORCE INDEX (${WORLD_ORDER_INDEX})
          ORDER BY score DESC, leaguePoint DESC, rankedAt ASC, id ASC LIMIT 1000`,
      );
      const country = await ds.query(
        `EXPLAIN SELECT id FROM leaderboard_entries FORCE INDEX (${COUNTRY_ORDER_INDEX})
          WHERE countryCode = 'vn' ORDER BY score DESC, leaguePoint DESC, rankedAt ASC, id ASC LIMIT 1000`,
      );
      expect(JSON.stringify(world)).not.toMatch(/filesort/i);
      expect(JSON.stringify(country)).not.toMatch(/filesort/i);
    });
  });

  describe('HTTP', () => {
    const userIds = ['u-1', 'u-2', 'u-3'];

    beforeEach(async () => {
      const infos = [
        { PlayerName: 'An', Level: 50, Wins: 4, Email: 'an@example.com' },
        { PlayerName: 'Binh', Level: 40, Wins: 0 },
        { Level: 10 },
      ];
      const countries = ['vn', 'us', null];
      for (let i = 0; i < 3; i += 1) {
        await ds.query('INSERT INTO players (id, userId, uid, countryCode, info) VALUES (?, ?, ?, ?, ?)', [
          `p-${i + 1}`, userIds[i], uidOf(10 + i), countries[i], JSON.stringify(infos[i]),
        ]);
      }
    });

    it('submit → flush → dựng bảng → top/rank trả đúng', async () => {
      const server = t.app.getHttpServer();
      await request(server).post('/leaderboard/submit').set('x-user-id', 'u-1').send({ score: 50 }).expect(201);
      await request(server).post('/leaderboard/submit').set('x-user-id', 'u-2').send({ score: 50, secondaryScore: 3 }).expect(201);
      const third = await request(server).post('/leaderboard/submit').set('x-user-id', 'u-3').send({ score: 10 }).expect(201);
      expect(third.body).toMatchObject({ uid: uidOf(12), score: 10, countryCode: null });

      await svc.refreshNow();

      const top = await request(server).get('/leaderboard/top?countryCode=WW').expect(200);
      expect(top.headers['content-type']).toMatch(/application\/json/);
      expect(top.body.boardTotal).toBe(3);
      // Bằng điểm 50: Binh có điểm phụ 3 nên đứng trên An.
      expect(top.body.entries.map((e: any) => e.uid)).toEqual([uidOf(11), uidOf(10), uidOf(12)]);
      expect(top.body.entries[0]).toMatchObject({ rank: 1, secondaryScore: 3, info: { PlayerName: 'Binh', Level: 40 } });
      expect(top.body.entries[0].info).not.toHaveProperty('Wins');
      expect(top.body.entries[1].info).toEqual({ PlayerName: 'An', Level: 50, Wins: 4 });
      expect(top.body.entries[1]).not.toHaveProperty('secondaryScore');
      expect(top.body.entries[2].info).toEqual({ PlayerName: 'Player', Level: 10 });

      const page = await request(server).get('/leaderboard/top?countryCode=ww&limit=1&offset=1').expect(200);
      expect(page.body).toMatchObject({ total: 1, boardTotal: 3 });
      expect(page.body.entries[0].uid).toBe(uidOf(10));

      const vn = await request(server).get('/leaderboard/top?countryCode=vn').expect(200);
      expect(vn.body.entries.map((e: any) => e.uid)).toEqual([uidOf(10)]);

      const rank = await request(server).get(`/leaderboard/rank?uid=${uidOf(10)}&countryCode=ww`).expect(200);
      expect(rank.body).toMatchObject({ rank: 2, total: 3, board: 'ww', countryCode: 'vn' });
      const notOnBoard = await request(server).get(`/leaderboard/rank?uid=${uidOf(10)}&countryCode=us`).expect(200);
      expect(['', 'null', '{}']).toContain(notOnBoard.text || '');
    });

    it('chặn request sai', async () => {
      const server = t.app.getHttpServer();
      await request(server).post('/leaderboard/submit').send({ score: 5 }).expect(403);
      await request(server).post('/leaderboard/submit').set('x-user-id', 'u-1').send({ score: -1 }).expect(400);
      await request(server).post('/leaderboard/submit').set('x-user-id', 'u-1').send({ score: 5, extra: 1 }).expect(400);
      await request(server).post('/leaderboard/submit').set('x-user-id', 'khong-co').send({ score: 5 }).expect(404);
      await request(server).get('/leaderboard/top?countryCode=xx').expect(400);
      await request(server).get('/leaderboard/top').expect(400);
      await request(server).get('/leaderboard/rank?uid=0123456789&countryCode=ww').expect(400);
    });
  });

  describe('sang mùa mới', () => {
    it('reset điểm phụ của mùa cũ và ghi mốc đã áp dụng', async () => {
      const players = Array.from({ length: 3 }, (_, i) => ({
        playerId: randomUUID(), uid: uidOf(80_000 + i), score: 5 + i, countryCode: null, secondaryScore: 4, season: 1,
      }));
      await store.upsertBatch(players);
      TestSeasons.season = 2;
      await t.moduleRef.get(LeaderboardMaintenanceService, { strict: false }).syncSeason();

      const rows = await ds.query('SELECT leaguePoint, leagueCycle FROM leaderboard_entries');
      expect(rows).toEqual(players.map(() => ({ leaguePoint: 0, leagueCycle: 2 })));
      expect(await t.moduleRef.get(TEST_REDIS).get('lb:v1:season-applied')).toBe('2');
    });
  });
});
