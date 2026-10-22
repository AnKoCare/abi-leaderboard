import { CanActivate, ExecutionContext, Global, INestApplication, Injectable, Module, ValidationPipe } from '@nestjs/common';
import { ScheduleModule } from '@nestjs/schedule';
import { Test, TestingModule } from '@nestjs/testing';
import { TypeOrmModule } from '@nestjs/typeorm';
import { randomUUID } from 'crypto';
import { createClient } from 'redis';
import { DataSource } from 'typeorm';
import { LEADERBOARD_MIGRATIONS, LeaderboardModule, LeaderboardModuleOptions, LeaderboardSeasonProvider } from '../../src';

export const MYSQL_URL = process.env.LB_TEST_MYSQL_URL ?? 'mysql://root:test@127.0.0.1:33999/lbtest';
export const REDIS_URL = process.env.LB_TEST_REDIS_URL ?? 'redis://127.0.0.1:63999';

export const TEST_REDIS = Symbol('TEST_REDIS');

/** Redis cho test — container dùng riêng cho test nên được phép FLUSHDB. */
@Global()
@Module({
  providers: [
    {
      provide: TEST_REDIS,
      useFactory: async () => {
        const client = createClient({ url: REDIS_URL });
        await client.connect();
        return client;
      },
    },
  ],
  exports: [TEST_REDIS],
})
export class TestRedisModule {}

/** Guard giả: header `x-user-id` = userId. */
@Injectable()
export class HeaderAuthGuard implements CanActivate {
  canActivate(context: ExecutionContext): boolean {
    const req = context.switchToHttp().getRequest();
    const id = req.headers['x-user-id'];
    if (!id) return false;
    req.user = { id: String(id) };
    return true;
  }
}

@Injectable()
export class TestSeasons implements LeaderboardSeasonProvider {
  static season = 1;
  async currentSeason(): Promise<number> {
    return TestSeasons.season;
  }
}

/** URL tới một database mới tinh trên cùng server MySQL của test. */
export async function createScratchDatabase(prefix: string): Promise<string> {
  const url = new URL(MYSQL_URL);
  const name = `${prefix}_${randomUUID().slice(0, 8)}`;
  const admin = new DataSource({ type: 'mysql', url: `${url.protocol}//${url.username}:${url.password}@${url.host}/` });
  await admin.initialize();
  await admin.query(`CREATE DATABASE \`${name}\``);
  await admin.destroy();
  url.pathname = `/${name}`;
  return url.toString();
}

export async function dropDatabase(dbUrl: string): Promise<void> {
  const url = new URL(dbUrl);
  const name = url.pathname.slice(1);
  const admin = new DataSource({ type: 'mysql', url: `${url.protocol}//${url.username}:${url.password}@${url.host}/` });
  await admin.initialize();
  await admin.query(`DROP DATABASE IF EXISTS \`${name}\``);
  await admin.destroy();
}

/** Bảng `players` giống template game-backend (đủ cột adapter đọc). */
export const PLAYERS_DDL = `
  CREATE TABLE players (
    id varchar(36) NOT NULL PRIMARY KEY,
    userId varchar(36) NOT NULL UNIQUE,
    uid varchar(10) NOT NULL UNIQUE,
    countryCode varchar(2) NULL,
    info json NULL
  ) ENGINE=InnoDB`;

export interface TestApp {
  app: INestApplication;
  moduleRef: TestingModule;
  dataSource: DataSource;
  dbUrl: string;
}

export async function createTestApp(overrides: Partial<LeaderboardModuleOptions> = {}): Promise<TestApp> {
  const dbUrl = await createScratchDatabase('lbtest');
  const moduleRef = await Test.createTestingModule({
    imports: [
      TypeOrmModule.forRoot({
        type: 'mysql',
        url: dbUrl,
        autoLoadEntities: true,
        synchronize: false,
        migrations: LEADERBOARD_MIGRATIONS,
      }),
      TestRedisModule,
      ScheduleModule.forRoot(),
      LeaderboardModule.forRoot({
        redisToken: TEST_REDIS,
        authGuard: HeaderAuthGuard,
        cron: false,
        writeBehind: { flushIntervalMs: 3_600_000 },
        secondaryScore: { enabled: true, acceptFromClient: true, seasonProvider: TestSeasons },
        profile: { fields: ['PlayerName', 'Level'], optionalFields: ['Wins'], defaults: { PlayerName: 'Player' } },
        ...overrides,
      }),
    ],
  }).compile();

  const app = moduleRef.createNestApplication();
  app.useGlobalPipes(new ValidationPipe({ whitelist: true, forbidNonWhitelisted: true, transform: true }));
  await app.init();

  const dataSource = moduleRef.get(DataSource);
  await dataSource.query(PLAYERS_DDL);
  await dataSource.runMigrations();
  const redis = moduleRef.get(TEST_REDIS);
  await redis.flushDb();
  return { app, moduleRef, dataSource, dbUrl };
}

export async function closeTestApp(t: TestApp | undefined): Promise<void> {
  if (!t) return;
  const redis = t.moduleRef.get(TEST_REDIS);
  await t.app.close();
  // Không để lại cache bảng: app khác trỏ vào cùng Redis + cùng prefix sẽ đọc nhầm.
  await redis.flushDb().catch(() => undefined);
  await redis.quit().catch(() => undefined);
  await dropDatabase(t.dbUrl);
}

/** uid 10 chữ số kiểu template, không bắt đầu bằng 0. */
export function uidOf(n: number): string {
  return String(1_000_000_000 + n);
}
