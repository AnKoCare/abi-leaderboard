import type { CanActivate, InjectionToken, ModuleMetadata, Type } from '@nestjs/common';
import { ISO_COUNTRY_CODES } from './constants/country-codes';
import { TEMPLATE_PROFILE_FIELDS, WORLD_BOARD } from './leaderboard.constants';
import type {
  LeaderboardJobRunner,
  LeaderboardPlayerResolver,
  LeaderboardProfileProvider,
  LeaderboardSeasonProvider,
} from './ports';
import type { LeaderboardTopEntry } from './types';

export interface LeaderboardProfileOptions {
  /** Key luôn có trên mỗi dòng; profile thiếu key thì lấy từ `defaults` (hoặc null). */
  fields?: string[];
  /**
   * Key chỉ gắn khi khác 0 / rỗng. Dùng cho stat mà phần lớn người chơi bằng 0 — đỡ
   * gánh hàng nghìn key "0" trên mỗi trang 1.000 dòng.
   */
  optionalFields?: string[];
  /** Giá trị mặc định khi profile thiếu key. */
  defaults?: Record<string, unknown>;
}

export interface LeaderboardCronOptions {
  /** Dựng sẵn cache bảng. Phải NGẮN hơn `cacheTtlSec`. Mặc định 20 phút, lệch mốc :00. */
  topBoards?: string;
  /** Dựng histogram tính hạng. Mặc định 15 phút, lệch mốc :00 và lệch lịch dựng bảng. */
  histograms?: string;
  /** Kiểm tra sang mùa mới (chỉ khi bật điểm phụ). Mặc định mỗi phút, giây 30. */
  season?: string;
}

export interface LeaderboardModuleOptions {
  /** Token của client node-redis v4 mà host đã đăng ký (template: `REDIS_CLIENT`). */
  redisToken: InjectionToken;
  /** Guard xác thực của host, gắn vào `POST submit` (template: `JwtAuthGuard`). */
  authGuard: Type<CanActivate>;
  /** Lấy userId từ request đã qua guard. Mặc định `req.user.id`. */
  getUserId?: (req: any) => string | null | undefined;
  /**
   * Module host cần import để resolve được `redisToken` / `authGuard` / provider tự viết.
   * Không cần nếu chúng đã là global.
   */
  imports?: ModuleMetadata['imports'];
  /**
   * Đăng ký module global để mọi module của host inject được `LeaderboardService` (vd.
   * PlayerModule đồng bộ Level, user-purge xoá dòng). Mặc định `true` — chỉ tắt khi host tự
   * re-export. Đừng gọi `forRoot` lần thứ hai ở module khác: sẽ thành hai bản, route trùng và
   * cron chạy hai lần.
   */
  isGlobal?: boolean;

  /** `'template'` (mặc định): dùng bảng `players` của template game-backend. */
  players?:
    | 'template'
    | {
        resolver: Type<LeaderboardPlayerResolver>;
        profileProvider: Type<LeaderboardProfileProvider>;
      };
  /** Tên bảng player khi `players: 'template'`. Mặc định `players`. */
  templatePlayersTable?: string;

  /** Prefix route. Mặc định `leaderboard` → `/leaderboard/submit|top|rank`. */
  routePrefix?: string;
  /** Prefix mọi key Redis của package. Mặc định `lb:`. */
  redisKeyPrefix?: string;
  /** Số dòng tối đa một board giữ/cache. Mặc định 1000. */
  topLimit?: number;
  /** `limit` khi client không gửi. Mặc định 100. */
  defaultPageSize?: number;
  /** TTL cache bảng, trang, hạng và cache player. Mặc định 3600 giây. */
  cacheTtlSec?: number;
  /** Mã quốc gia hợp lệ cho board quốc gia. Mặc định toàn bộ ISO alpha-2. */
  countryCodes?: readonly string[];
  /** Định dạng uid cho `GET /rank`. Mặc định 10 chữ số không bắt đầu bằng 0 (template). */
  uidPattern?: RegExp;
  profile?: LeaderboardProfileOptions;

  /** Điểm phụ để phân định khi bằng điểm. Mặc định TẮT. */
  secondaryScore?: {
    enabled: boolean;
    /** Cho client gửi điểm phụ qua `POST submit`. Mặc định false: chỉ code server gửi. */
    acceptFromClient?: boolean;
    /** Không truyền = mùa luôn là 1 (điểm phụ không bao giờ reset). */
    seasonProvider?: Type<LeaderboardSeasonProvider>;
  };

  /** Ghi điểm qua hàng đợi Redis rồi gom lô xuống MySQL. Mặc định BẬT. */
  writeBehind?: {
    enabled?: boolean;
    flushIntervalMs?: number;
    batchSize?: number;
    maxRoundsPerTick?: number;
  };
  /** Đọc thống kê từ replica (`createQueryRunner('slave')`). Mặc định false. */
  readReplica?: boolean;
  /** `false` = không đăng ký cron, host tự gọi `LeaderboardService.refreshNow()`. */
  cron?: false | LeaderboardCronOptions;
  /** Mặc định: biến môi trường `RUN_BACKGROUND_JOBS` + khoá Redis riêng của package. */
  jobRunner?: Type<LeaderboardJobRunner>;
  /** Sửa từng dòng trước khi cache (vd. gắn bậc danh hiệu). Chạy lúc dựng bảng, không chạy mỗi request. */
  decorateEntry?: (entry: LeaderboardTopEntry, profile: Record<string, unknown> | undefined) => void;
  /** Tag Swagger. Mặc định `Leaderboard`. */
  swaggerTag?: string;
}

export interface ResolvedLeaderboardOptions {
  redisToken: InjectionToken;
  authGuard: Type<CanActivate>;
  getUserId: (req: any) => string | null | undefined;
  imports: NonNullable<ModuleMetadata['imports']>;
  isGlobal: boolean;
  players: NonNullable<LeaderboardModuleOptions['players']>;
  templatePlayersTable: string;
  routePrefix: string;
  redisKeyPrefix: string;
  topLimit: number;
  defaultPageSize: number;
  cacheTtlSec: number;
  countryCodes: ReadonlySet<string>;
  uidPattern: RegExp;
  profile: Required<LeaderboardProfileOptions>;
  secondaryScore: {
    enabled: boolean;
    acceptFromClient: boolean;
    seasonProvider?: Type<LeaderboardSeasonProvider>;
  };
  writeBehind: Required<NonNullable<LeaderboardModuleOptions['writeBehind']>>;
  readReplica: boolean;
  cron: false | Required<LeaderboardCronOptions>;
  jobRunner?: Type<LeaderboardJobRunner>;
  decorateEntry?: LeaderboardModuleOptions['decorateEntry'];
  swaggerTag: string;
}

const SAFE_IDENTIFIER = /^[A-Za-z0-9_]+$/;

function positiveInt(value: number | undefined, fallback: number, name: string): number {
  if (value === undefined) return fallback;
  if (!Number.isInteger(value) || value <= 0) {
    throw new Error(`[abi-leaderboard] ${name} phải là số nguyên dương, nhận ${value}`);
  }
  return value;
}

/** Điền mặc định và kiểm tra option một lần lúc khởi động — sai thì app không lên. */
export function resolveLeaderboardOptions(
  options: LeaderboardModuleOptions,
): ResolvedLeaderboardOptions {
  if (!options?.redisToken) throw new Error('[abi-leaderboard] thiếu redisToken');
  if (!options.authGuard) throw new Error('[abi-leaderboard] thiếu authGuard');

  const players = options.players ?? 'template';
  const templatePlayersTable = options.templatePlayersTable ?? 'players';
  if (!SAFE_IDENTIFIER.test(templatePlayersTable)) {
    throw new Error(`[abi-leaderboard] templatePlayersTable không hợp lệ: ${templatePlayersTable}`);
  }

  const countryCodes = new Set(
    (options.countryCodes ?? ISO_COUNTRY_CODES).map((c) => c.trim().toLowerCase()),
  );
  // 'ww' là mã của board World; để nó lọt vào danh sách quốc gia thì một người chọn
  // quốc gia "ww" sẽ ghi vào đúng board World.
  countryCodes.delete(WORLD_BOARD);

  const topLimit = positiveInt(options.topLimit, 1000, 'topLimit');
  const defaultPageSize = Math.min(
    positiveInt(options.defaultPageSize, 100, 'defaultPageSize'),
    topLimit,
  );

  const profileFields =
    options.profile?.fields ?? (players === 'template' ? [...TEMPLATE_PROFILE_FIELDS] : []);

  return {
    redisToken: options.redisToken,
    authGuard: options.authGuard,
    getUserId: options.getUserId ?? ((req) => req?.user?.id),
    imports: options.imports ?? [],
    isGlobal: options.isGlobal ?? true,
    players,
    templatePlayersTable,
    routePrefix: options.routePrefix ?? 'leaderboard',
    redisKeyPrefix: options.redisKeyPrefix ?? 'lb:',
    topLimit,
    defaultPageSize,
    cacheTtlSec: positiveInt(options.cacheTtlSec, 3600, 'cacheTtlSec'),
    countryCodes,
    uidPattern: options.uidPattern ?? /^[1-9][0-9]{9}$/,
    profile: {
      fields: profileFields,
      optionalFields: options.profile?.optionalFields ?? [],
      defaults: options.profile?.defaults ?? {},
    },
    secondaryScore: {
      enabled: options.secondaryScore?.enabled ?? false,
      acceptFromClient: options.secondaryScore?.acceptFromClient ?? false,
      seasonProvider: options.secondaryScore?.seasonProvider,
    },
    writeBehind: {
      enabled: options.writeBehind?.enabled ?? true,
      flushIntervalMs: positiveInt(options.writeBehind?.flushIntervalMs, 2000, 'writeBehind.flushIntervalMs'),
      batchSize: positiveInt(options.writeBehind?.batchSize, 500, 'writeBehind.batchSize'),
      maxRoundsPerTick: positiveInt(options.writeBehind?.maxRoundsPerTick, 5, 'writeBehind.maxRoundsPerTick'),
    },
    readReplica: options.readReplica ?? false,
    cron:
      options.cron === false
        ? false
        : {
            topBoards: options.cron?.topBoards ?? '0 13-53/20 * * * *',
            histograms: options.cron?.histograms ?? '0 5-50/15 * * * *',
            season: options.cron?.season ?? '30 * * * * *',
          },
    jobRunner: options.jobRunner,
    decorateEntry: options.decorateEntry,
    swaggerTag: options.swaggerTag ?? 'Leaderboard',
  };
}
