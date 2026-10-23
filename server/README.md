# @abigames/leaderboard-server

Bảng xếp hạng người chơi (World + theo quốc gia) cho backend **NestJS 10 + TypeORM +
MySQL 8 + Redis (node-redis v4)**. Cơ chế chịu tải lấy nguyên từ module leaderboard của
Colony Flow (vài triệu người chơi, khoảng 2.000 rps); bản package là code viết lại, đã
qua unit test và integration test trên MySQL + Redis thật, **chưa chạy production**.

- `POST /leaderboard/submit` — ghi điểm (Level) của người chơi đang đăng nhập; điểm **chỉ tăng**.
- `GET /leaderboard/top?countryCode=ww|vn|…&limit=&offset=` — top tới 1.000 dòng, có phân trang.
- `GET /leaderboard/rank?uid=&countryCode=` — hạng của một uid.
- Tuỳ chọn: điểm phụ để phân định khi bằng điểm, có reset theo mùa.
- Blocklist uid gian lận; API cho code server của host.

Chịu tải nhờ: ghi qua hàng đợi Redis rồi gom lô, cache bảng hai tầng kèm cron dựng sẵn,
và tính hạng bằng histogram điểm thay cho COUNT trên bảng hàng triệu dòng.

## Yêu cầu của host

| Cần | Template game-backend |
|---|---|
| NestJS 10, `@nestjs/typeorm` 10, TypeORM 0.3, mysql2 | có |
| Client node-redis v4 đăng ký qua một injection token | `REDIS_CLIENT` (global) |
| `ScheduleModule.forRoot()` | có trong `AppModule` |
| Guard xác thực gắn `req.user.id` | `JwtAuthGuard` |
| `@nestjs/swagger` 7, class-validator, class-transformer, `cron` 3 | có |
| Global `ValidationPipe({ transform: true })` | có trong `main.ts` |
| Bảng người chơi: `id`, `userId`, `uid`, `countryCode`, `info` (JSON) | bảng `players` |

Bảng người chơi khác cấu trúc vẫn dùng được: tự cài `LeaderboardPlayerResolver` và
`LeaderboardProfileProvider` (xem [Port](#port-cho-host-không-dùng-bảng-players-của-template)).

## Cài đặt

Package là nội bộ, không đăng lên npm. Cách gọn nhất là để file tarball ngay trong
repo host: build Docker không cần quyền truy cập git hay registry nào.

```bash
# trong repo abi-leaderboard
cd server && npm install && npm run pack:release      # → abigames-leaderboard-server-0.1.1.tgz

# trong repo host
mkdir -p vendor && cp …/abigames-leaderboard-server-0.1.1.tgz vendor/
npm install ./vendor/abigames-leaderboard-server-0.1.1.tgz
```

`package.json` của host sẽ có `"@abigames/leaderboard-server": "file:vendor/…tgz"`, nên
`Dockerfile` phải copy `vendor/` **trước** bước cài package. Dockerfile của template copy
`package*.json` rồi `npm install` trước khi `COPY . .` — thêm một dòng:

```dockerfile
COPY package*.json .npmrc ./
COPY vendor ./vendor          # ← thêm
RUN npm install
```

`cron` là peer dependency nhưng template không khai báo trực tiếp — nó đã có sẵn qua
`@nestjs/schedule`, npm tự dùng bản đó.

## Cài vào template game-backend

Template đã có sẵn một module leaderboard cũ, dùng cùng route và cùng bảng. Package
**thay thế** module đó và dùng lại nguyên bảng cùng dữ liệu đang có.

Toàn bộ các bước dưới đây đã chạy thử trên một bản template sạch (cài tarball, migration trên
DB trống, app chạy cluster 2 worker, gọi đủ đăng nhập → đổi quốc gia → lưu Level → submit →
cron dựng bảng → top / rank → purge). Diff đầy đủ của lần đó nằm ở
[`examples/template-integration/template-game-backend.patch`](examples/template-integration/template-game-backend.patch)
(không gồm `package.json` / `package-lock.json` do `npm install` tự sửa).

### 1. Đăng ký module

```ts
// src/app.module.ts
import { LeaderboardModule } from '@abigames/leaderboard-server';
import { REDIS_CLIENT } from './redis/redis.module';
import { JwtAuthGuard } from './common/guards/jwt-auth.guard';

@Module({
  imports: [
    // …
    LeaderboardModule.forRoot({
      redisToken: REDIS_CLIENT,
      authGuard: JwtAuthGuard,
      jobRunner: HostJobRunner, // tuỳ chọn, xem bước 4
    }),
  ],
})
export class AppModule {}
```

- Bỏ `LeaderboardModule` cũ khỏi `imports` của **cả** `AppModule` lẫn `PlayerModule`
  (`src/modules/player/player.module.ts`). Thư mục `src/modules/leaderboard` để lại hay xoá
  đều được — không còn ai import thì nó không chạy.
- Module của package là **global**: mọi module của host inject được `LeaderboardService` mà
  không cần import gì. Chỉ gọi `forRoot` **một lần** ở `AppModule`.
- Entity cũ `LeaderboardEntry` / `LeaderboardBlockedUid` trong `src/database/entities` cứ để
  nguyên (user-purge còn dùng): hai bên cùng trỏ một bảng, đã chạy thử không xung đột.

### 2. Nối các chỗ đang gọi module cũ

Tìm các chỗ đang gọi: `grep -rn "leaderboardService\.\|queueScore\|drainScores" src`.
Đổi import `'../leaderboard/leaderboard.service'` thành `'@abigames/leaderboard-server'`.

**`PlayerService.updatePlayerInfo`** (`PUT /player/info`) — đồng bộ Level ngay trên đường
request, cho cả nhánh có hàng đợi lẫn nhánh ghi thẳng:

```ts
const queued = await this.infoCache.queueUpdate({ … });
if (!queued) {
  await this.playerRepository.update(player.id, { info: nextInfo });
}
if (level != null) {
  await this.leaderboardService.syncScore(userId, level);
}
```

- `syncScore` **không ném lỗi** khi uid bị chặn (trả `false`), nên người bị chặn vẫn lưu
  được profile. `submitForPlayer` thì ném 403 — chỉ dùng khi muốn báo lỗi cho client.
- Truyền **`userId`**, đừng truyền `{ playerId, uid, countryCode }` lấy từ
  `resolvePlayerByUserId`: player trong cache của template chỉ có `id` và `uid`, nên
  `countryCode` luôn là `null` và người chơi mới chọn quốc gia sẽ không có mặt ở bảng quốc gia.
  Truyền `userId` thì package tự tra bảng `players` (có cache riêng, được `syncCountry` xoá).

**`PlayerInfoFlushService`** — bỏ hẳn phần điểm: lời gọi và hàm `writeLeaderboardBatch`,
`flushScores` / `drainScores` / `scoreTimer`, và `LeaderboardService` trong constructor. Đừng
chuyển `writeLeaderboardBatch` sang gọi `syncScore`: hàng đợi info chỉ giữ **bản mới nhất** của
mỗi player, nên hai lần lưu liền nhau (Level 80 rồi 10) sẽ làm mất số 80; hàng đợi của package
thì giữ **số cao nhất**. Các hàm `queueScore` / `claimScores` / `requeueScores` của
`PlayerInfoCacheService` thành thừa, xoá sau cũng được.

| Chỗ gọi cũ | Thay bằng |
|---|---|
| `syncScoreFromLevel(…)` + `upsertScoresBatch(…)` | `syncScore(userId, level)` như trên |
| `syncCountryCode(player.id, player.uid, cc)` (`PATCH /player/country`) | `syncCountry(player.id, cc, userId)` |
| `user-purge` xoá `leaderboard_entries` | giữ câu DELETE, thêm `removePlayer` sau transaction (dưới) |

```ts
// UserPurgeService.purgeByUid, sau transaction
for (const pid of playerIds) {
  await this.saveCache.invalidatePlayer(pid, userId);
  await this.leaderboard.removePlayer(pid, userId); // bỏ cả điểm đang chờ trong hàng đợi
}
```

### 3. Migration

Template chạy migration bằng `npm run migration:run` (`start-app.sh` gọi lúc container khởi
động), tức là đọc **`ormconfig.js`** — sửa ở đó:

```js
// ormconfig.js
migrations: [
  'dist/database/migrations/*.js',
  ...require('@abigames/leaderboard-server').LEADERBOARD_MIGRATIONS,
],
```

`migrations` trong `src/config/database.config.ts` không được dùng (app không tự chạy
migration), thêm vào đó cũng không hại. Migration của package có mốc `1790000000000`, chạy sau
mọi migration của template. Chạy lại bao nhiêu lần cũng được:
- DB chưa có bảng: tạo đầy đủ.
- Bảng có từ template cũ: chỉ thêm cột / index còn thiếu, giữ nguyên dữ liệu và khoá
  ngoại cũ. Bản rất cũ (chưa có cột `rankedAt`) sẽ UPDATE toàn bảng một lần, nên chạy
  lúc thấp điểm.

Template bật `autoLoadEntities` nên không phải khai báo entity. Host không bật thì thêm
`LEADERBOARD_ENTITIES` vào `entities`.

### 4. Job nền khi chạy cluster

Worker flush và cron chỉ chạy ở tiến trình mà `jobRunner.isJobRunner()` trả `true`. Mặc
định package đọc `RUN_BACKGROUND_JOBS !== '0'`, đúng quy ước của `src/main.ts` trong
template (chỉ worker 0 có giá trị `'1'`).

Nên cắm khoá job nặng chung của template vào để job của BXH xếp hàng cùng các job
khác, thay vì dùng khoá riêng của package:

```ts
// src/leaderboard/host-job-runner.ts
import { Inject, Injectable, Logger } from '@nestjs/common';
import type { RedisClientType } from 'redis';
import type { LeaderboardJobRunner } from '@abigames/leaderboard-server';
import { REDIS_CLIENT } from '../redis/redis.module';
import { isBackgroundJobRunner } from '../common/background-jobs';
import { HEAVY_JOB_SCHEDULED_WAIT_MS, runExclusiveHeavyJob } from '../common/heavy-job-lock';

@Injectable()
export class HostJobRunner implements LeaderboardJobRunner {
  private readonly logger = new Logger('LeaderboardJobs');
  constructor(@Inject(REDIS_CLIENT) private readonly redis: RedisClientType) {}

  isJobRunner() {
    return isBackgroundJobRunner();
  }

  runExclusive(job: string, work: () => Promise<void>) {
    return runExclusiveHeavyJob(this.redis, this.logger, job, { waitMs: HEAVY_JOB_SCHEDULED_WAIT_MS }, work);
  }
}
```

### 5. nginx

Xem [`examples/nginx-leaderboard.conf`](examples/nginx-leaderboard.conf):
- `GET /top` là API công khai, cache chung cho mọi người (khoá cache bỏ header
  `Authorization`).
- `GET /rank` có `limit_conn`, để một đợt tra hạng không đè được MySQL.

### 6. Lúc chuyển đổi

Hàng đợi điểm của module cũ (`game:score-pending`) **không** được package đọc. Trước khi
deploy, kiểm tra `HLEN game:score-pending` đã về 0. Nếu còn, điểm trong đó sẽ được bù khi
người chơi gửi lại ở lần thắng level kế tiếp (điểm chỉ tăng nên không có gì sai). Cache
cũ (`lb:top:v4:*`, `lb:rank:v4:*`, …) tự hết hạn sau tối đa 1 giờ.

## Tuỳ chọn

| Option | Mặc định | Ghi chú |
|---|---|---|
| `redisToken` | **bắt buộc** | token client node-redis v4 |
| `authGuard` | **bắt buộc** | guard của host, gắn vào `POST submit` |
| `getUserId` | `req => req.user.id` | |
| `imports` | `[]` | module cần import để resolve token / guard / provider |
| `isGlobal` | `true` | `LeaderboardService` inject được ở mọi module của host |
| `players` | `'template'` | hoặc `{ resolver, profileProvider }` |
| `templatePlayersTable` | `'players'` | |
| `routePrefix` | `'leaderboard'` | |
| `redisKeyPrefix` | `'lb:'` | mọi key dạng `lb:v1:…` |
| `topLimit` | `1000` | số dòng tối đa mỗi board |
| `defaultPageSize` | `100` | `limit` khi client không gửi |
| `cacheTtlSec` | `3600` | TTL cache bảng / trang / hạng / player |
| `countryCodes` | ISO alpha-2 | `'ww'` luôn là World |
| `uidPattern` | `/^[1-9][0-9]{9}$/` | uid 10 chữ số của template |
| `profile.fields` | `Level, PlayerName, AvatarIndex, FrameIndex, BadgeIndex` (template) | key luôn có trong `info` |
| `profile.optionalFields` | `[]` | key chỉ gắn khi khác 0 / rỗng |
| `profile.defaults` | `{}` | giá trị khi profile thiếu key |
| `secondaryScore.enabled` | `false` | điểm phụ phân định khi bằng điểm |
| `secondaryScore.acceptFromClient` | `false` | cho client gửi điểm phụ qua `submit` |
| `secondaryScore.seasonProvider` | mùa luôn = 1 | đổi mùa = điểm phụ về 0 |
| `writeBehind.enabled` | `true` | tắt = ghi thẳng MySQL mỗi request |
| `writeBehind.flushIntervalMs` / `batchSize` / `maxRoundsPerTick` | `2000` / `500` / `5` | |
| `readReplica` | `false` | đọc thống kê từ `createQueryRunner('slave')` |
| `cron` | 20 phút dựng bảng (`0 13-53/20 * * * *`), 15 phút histogram (`0 5-50/15 * * * *`), mỗi phút kiểm tra mùa | `false` = host tự gọi `refreshNow()` |
| `jobRunner` | `RUN_BACKGROUND_JOBS` + khoá Redis | |
| `decorateEntry` | — | sửa từng dòng lúc dựng bảng (vd. gắn bậc danh hiệu) |

## API cho code server

```ts
constructor(private readonly leaderboard: LeaderboardService) {}

await leaderboard.syncScore(userId, level);             // đồng bộ khi lưu profile; uid bị chặn → false, không ném
await leaderboard.submitForPlayer({ playerId, uid, countryCode }, level, secondaryScore?); // ném 403 nếu bị chặn
await leaderboard.syncCountry(playerId, 'vn', userId);   // đổi quốc gia
await leaderboard.removePlayer(playerId, userId);        // BẮT BUỘC khi xoá tài khoản
await leaderboard.blockUid(uid, 'lý do');                // cấm + đưa điểm về 0
await leaderboard.unblockUid(uid);
await leaderboard.getTop('ww', 100, 0);                  // object
await leaderboard.getRank(uid, 'vn');                    // null nếu không có trên board
await leaderboard.refreshNow();                          // ghi hết hàng đợi + dựng lại mọi bảng (nặng)
```

Package không có route admin: blocklist và `refreshNow` do host tự gắn vào trang admin
của mình.

## HTTP API

`GET /leaderboard/top?countryCode=ww&limit=100&offset=0`

```json
{
  "countryCode": "ww",
  "total": 100,
  "boardTotal": 1000,
  "entries": [
    { "rank": 1, "uid": "1234567890", "score": 1800, "countryCode": "vn",
      "secondaryScore": 12, "info": { "Level": 1800, "PlayerName": "An", "AvatarIndex": 3 } }
  ]
}
```

- `total` = số dòng trả về; `boardTotal` = tổng số dòng của board (≤ `topLimit`).
- `secondaryScore` chỉ có khi bật tính năng và giá trị khác 0.
- Thứ tự: `score` giảm dần → `secondaryScore` giảm dần → ai đạt trước đứng trên → `id`.

`GET /leaderboard/rank?uid=1234567890&countryCode=vn`

```json
{ "rank": 42, "uid": "1234567890", "score": 850, "secondaryScore": 0,
  "countryCode": "vn", "board": "vn", "total": 51234 }
```

uid không có trên board đó thì body rỗng (null).

`POST /leaderboard/submit` (Bearer) — body `{ "score": 851 }`; trả về
`{ playerId, uid, score, secondaryScore, countryCode, scoreUpdatedAt }`.

**Độ trễ:**
- Điểm vào DB sau một nhịp flush (2 giây).
- Lên bảng và đổi hạng sau lượt dựng kế tiếp: tối đa khoảng 20 phút cho bảng; hạng cache tối đa 1 giờ.
- Hạng của người vừa lên một mức điểm đã có trong histogram (dựng 15 phút một lần) là số
  xấp xỉ cho tới lượt dựng sau; `total` lúc đó cũng là số của lần dựng trước.
- Bảng xếp hạng không cần realtime; đây là cái giá để `/top` và `/rank` chịu được tải lớn.

**Thử tay trên máy dev:** gửi điểm xong mà `/top` chưa đổi là đúng thiết kế — bảng chỉ dựng
lại theo cron. Muốn thấy ngay thì cho cron chạy dày ở môi trường dev:

```ts
LeaderboardModule.forRoot({
  // …
  cron: process.env.NODE_ENV === 'production'
    ? undefined
    : { topBoards: '*/30 * * * * *', histograms: '*/30 * * * * *' },
}),
```

## Vận hành

- **Redis:**
  - **Nhiều app / môi trường / game dùng chung một Redis thì mỗi bên PHẢI đặt `redisKeyPrefix` riêng** (vd. `lb:colony:`, `lb:fishsort-staging:`). Trùng prefix là bên này đọc bảng bên kia đã cache, và hai worker flush rút chung một hàng đợi.
  - Mỗi board khoảng 200 KB × (board World + số quốc gia có người chơi), cộng các trang đã cache.
  - Mỗi người đang hoạt động thêm khoảng 2 key nhỏ (`player`, `last`), TTL 1 giờ và 6 giờ.
  - Không key nào phải tìm bằng SCAN.
- **MySQL:**
  - Hai index thứ tự `(score DESC, leaguePoint DESC, rankedAt ASC)` và `(countryCode, score DESC, leaguePoint DESC, rankedAt ASC)`.
  - Unique trên `playerId`: câu upsert dựa vào nó.
- **Cron:**
  - Lịch mặc định lệch khỏi mốc :00 / :30 và lệch nhau.
  - Không chạy lúc khởi động: deploy cuốn chiếu giờ cao điểm không làm MySQL quá tải.
- **Chặn uid gian lận:** gọi `blockUid(uid, lý do)` (cấm + đưa điểm về 0 ngay). Insert tay
  vào `leaderboard_blocked_uids` thì phải tự điền `id` (`UUID()`), mỗi tiến trình nạp lại
  danh sách sau tối đa 60 giây, và điểm cũ vẫn nằm trên bảng cho tới khi bạn tự đưa về 0.
- **Worker flush:**
  - Dòng hỏng vì chính dữ liệu của nó (quá dài, khoá ngoại tới player đã bị xoá…) bị bỏ kèm log `bỏ điểm không ghi được`.
  - Lỗi tạm thời (khoá, mất kết nối) thì cả lô được trả về hàng đợi để thử lại.
- **Giới hạn:** histogram tính hạng theo từng giá trị điểm nên chỉ rẻ khi điểm có ít giá
  trị phân biệt (Level). Game có điểm dải rộng (hàng triệu) cần thêm histogram theo dải
  điểm — chưa có trong bản này.

## Port cho host không dùng bảng `players` của template

```ts
@Injectable()
class MyPlayers implements LeaderboardPlayerResolver, LeaderboardProfileProvider {
  async findByUserId(userId: string) { /* → { playerId, uid, countryCode } | null */ }
  async findByUid(uid: string) { /* tuỳ chọn */ }
  async getProfiles(ids: string[]) { /* MỘT truy vấn IN (...) → Map<playerId, object> */ }
}

LeaderboardModule.forRoot({
  redisToken: REDIS_CLIENT,
  authGuard: JwtAuthGuard,
  players: { resolver: MyPlayers, profileProvider: MyPlayers },
  profile: { fields: ['name', 'avatar'] },
});
```

## Phát triển package

```bash
npm install
npm test                 # unit test
npm run test:integration # MySQL + Redis thật — xem test/integration/README.md
npm run build            # → dist/
npm run pack:release     # → abigames-leaderboard-server-<version>.tgz
```
