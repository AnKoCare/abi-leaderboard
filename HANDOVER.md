# Bàn giao: abi-leaderboard

Bảng xếp hạng người chơi (World + từng quốc gia) theo **Level**, đóng gói từ Colony Flow để
game khác của AbiGames dùng lại. Tài liệu này dành cho dev nhận package: đọc hết là tích hợp
được, không cần hỏi lại.

| Phần | Bản | Kiểm chứng |
|---|---|---|
| Server `@abigames/leaderboard-server` (`server/`) | **0.1.1** | 19 unit + 19 integration test trên MySQL 8 / Redis 7 thật (Node 20; unit cả Node 18); cài thử vào một bản template game-backend sạch, chạy cluster 2 worker, `smoke-test.sh` qua |
| Unity `com.abigames.leaderboard` (`unity/com.abigames.leaderboard`) | **0.2.0** | 47 EditMode + 9 PlayMode test trên Unity 6000.3; cài thử vào project Unity 6 trống, chạy sample, chụp ảnh |

**Chưa chạy production.** Cơ chế chịu tải giữ nguyên bản đang chạy ở Colony Flow (vài triệu
người chơi, khoảng 2.000 rps), nhưng code package là viết lại và chưa đo tải riêng.

---

## 1. Có gì, không có gì

**Có:**
- Ghi điểm từ client (`POST /leaderboard/submit`) hoặc từ code server (khi lưu profile).
- Điểm chỉ tăng.
- Top tới 1.000 dòng mỗi board, có phân trang.
- Hạng của một uid bất kỳ.
- Blocklist uid gian lận.
- Điểm phụ tuỳ chọn để phân định khi bằng điểm, có reset theo mùa (mặc định tắt).
- Client Unity: core gọi API, tải board kèm cache đĩa, gửi điểm có gộp và thử lại, UI uGUI
  dựng sẵn, sample chạy với server giả.

**Không:**
- BXH Team.
- Route admin: host tự gắn `blockUid` / `refreshNow` vào trang admin của mình.
- Metrics Prometheus riêng: chỉ có log (mục 5).
- Điểm dải rộng (hàng triệu giá trị khác nhau): histogram tính hạng theo từng mức điểm, chỉ rẻ
  với Level.
- Colony Flow **không** chuyển sang package; hai bản sẽ lệch dần.

## 2. Chạy thế nào

```
 Unity                               Host NestJS + package                         Lưu trữ
 ─────                               ─────────────────────                         ───────
 Submitter.Submit(level) ──POST /submit──────┐
                                             ├──► hàng đợi Redis ──(worker 2 giây,──► MySQL
 (game lưu profile) ──PUT /player/info──► syncScore(userId, level)┘   lô 500)          leaderboard_entries
                                                                                       │
                                     cron 15 phút: histogram điểm ◄──────────────────┤
                                     cron 20 phút: dựng top 1.000 mỗi board ◄────────┘
                                                  │
 BoardLoader ◄──GET /top──── cache Redis (+ nginx cache) ┘
 BoardView   ◄──GET /rank─── histogram + đếm trong một mức điểm, cache 1 giờ
```

- **Hàng đợi** giữ số cao nhất của mỗi player, nên gửi nhiều lần liên tiếp chỉ thành một lần ghi.
- Worker flush và cron chỉ chạy ở **một tiến trình mỗi container**: worker 0 theo quy ước
  `RUN_BACKGROUND_JOBS` của template.
- **Độ trễ là thiết kế:** điểm vào DB sau khoảng 2 giây, lên bảng sau tối đa khoảng 20 phút,
  hạng cache tối đa 1 giờ. Đổi lại, `/top` và `/rank` không bao giờ quét bảng hàng triệu dòng
  theo request.

## 3. Tích hợp vào game mới — khoảng nửa ngày

Giả định host dựng từ template game-backend (NestJS + TypeORM + MySQL + Redis, bảng `players`
có `userId`, `uid`, `countryCode`, `info` JSON). Host khác cấu trúc thì thêm khoảng 1 giờ viết
port (server README → *Port cho host không dùng bảng players*).

### Server — khoảng 2 giờ

Làm theo [`server/README.md`](server/README.md) → *Cài vào template game-backend*. Diff thật
của lần cài thử nằm ở
[`server/examples/template-integration/template-game-backend.patch`](server/examples/template-integration/template-game-backend.patch),
áp được gần như nguyên vẹn nếu template của bạn cùng đời.

| # | Việc | Xong khi |
|---|---|---|
| 1 | Lấy tarball `abigames-leaderboard-server-0.1.1.tgz` (build: `cd server && npm ci && npm run pack:release`, Node 18 trở lên), chép vào `vendor/` của host, `npm install ./vendor/…tgz`. Thêm `COPY vendor ./vendor` trước `RUN npm install` trong Dockerfile | `docker build` qua |
| 2 | `LeaderboardModule.forRoot({ redisToken, authGuard, jobRunner })` trong `AppModule`; bỏ module leaderboard cũ khỏi `AppModule` **và** `PlayerModule`; thêm `HostJobRunner` | `nest build` qua |
| 3 | `PUT /player/info` gọi `syncScore(userId, level)`; `PATCH /player/country` gọi `syncCountry(playerId, cc, userId)`; user-purge gọi `removePlayer`; bỏ phần điểm trong `PlayerInfoFlushService` | `grep -rn "leaderboardService\.\|queueScore" src` chỉ còn lời gọi API mới |
| 4 | Thêm `LEADERBOARD_MIGRATIONS` vào `ormconfig.js`, chạy `npm run migration:run` | log có `LeaderboardSchema1790000000000 has been executed` |
| 5 | Cho cron chạy dày khi không phải production (README → *Thử tay trên máy dev*), chạy app | log worker 0 có `worker flush điểm chạy` |
| 6 | `BASE=http://localhost:3000/api/v1 ./server/examples/smoke-test.sh` | in `OK — package chạy đúng trên host này` |
| 7 | Trước khi lên production: áp cấu hình nginx từ [`server/examples/nginx-leaderboard.conf`](server/examples/nginx-leaderboard.conf); dùng chung Redis với app khác thì đặt `redisKeyPrefix` riêng | |

### Client Unity — khoảng 2 giờ

Làm theo [`unity/com.abigames.leaderboard/README.md`](unity/com.abigames.leaderboard/README.md).

| # | Việc | Xong khi |
|---|---|---|
| 1 | Thêm UniTask và package vào `Packages/manifest.json` (git URL kèm tag, hoặc `file:`). Cần Unity 6 | Console không lỗi biên dịch |
| 2 | Import *TMP Essential Resources* (project chưa có), import sample **UI Demo**, gắn `LeaderboardUIDemo` vào scene trống, bấm Play | thấy bảng 1.000 dòng chạy với server giả |
| 3 | Lúc boot, sau khi đăng nhập: `LeaderboardRuntime.Initialize(config)`, với `BaseUrl` **gồm** `/api/v1`, `AccessTokenProvider`, `SelfUidProvider` | |
| 4 | *Tools → AbiGames → Leaderboard → Create Sample Panel*, lưu thành prefab, đặt vào popup của game; gán `PlayerCountryProvider`, `SelfProfileProvider`; đổi ô bằng cách kế thừa `LeaderboardCellBase` nếu cần | mở popup thấy dữ liệu server thật |
| 5 | Gửi điểm: nếu game đã lưu Level qua `PUT /player/info` thì server tự đồng bộ, không cần gì thêm. Muốn điểm vào nhanh hơn hoặc game không lưu profile lên server thì gọi `LeaderboardRuntime.Submitter.Submit(level)` khi thắng level | sau một lượt cron, người chơi thấy mình trên bảng |

### Kiểm tra cuối — khoảng 1 giờ

- Tài khoản mới: chọn quốc gia → thắng vài level → sau lượt cron có mặt ở **cả** board World
  lẫn board quốc gia.
- Người ngoài top 1.000: ô "hạng của tôi" vẫn hiện hạng.
- Mất mạng lúc mở bảng: hiện dữ liệu cache kèm dòng "dữ liệu lúc …".
- Chặn thử một uid bằng `blockUid`: không còn trên bảng sau lượt dựng kế tiếp, nhưng lưu profile
  vẫn bình thường.
- Xoá tài khoản: dòng biến mất sau lượt dựng kế tiếp.

## 4. Những chỗ dễ sai (đã gặp thật khi cài thử)

| Triệu chứng | Nguyên nhân | Cách đúng |
|---|---|---|
| `docker build` hỏng ở `npm install` | `vendor/` chưa được copy vào image | `COPY vendor ./vendor` trước `RUN npm install` |
| Bảng mới không có trong DB | khai báo migration ở `database.config.ts`; template chạy migration bằng CLI đọc `ormconfig.js` | thêm vào `ormconfig.js` |
| `Nest can't resolve dependencies … LeaderboardService` | bản 0.1.0 không global | dùng 0.1.1; chỉ gọi `forRoot` một lần |
| Người chơi mới không có ở bảng quốc gia | truyền `{ playerId, uid, countryCode }` lấy từ cache player của template — cache này không có `countryCode` | gọi `syncScore(userId, level)` |
| Lưu Level 80 rồi 10 liền nhau, bảng ghi 10 | đồng bộ điểm từ worker flush info — hàng đợi info chỉ giữ bản mới nhất | gọi `syncScore` trên đường request, bỏ phần điểm khỏi worker flush |
| Người bị chặn không lưu được profile | dùng `submitForPlayer` (ném 403) ở đường lưu profile | dùng `syncScore` (bị chặn → `false`) |
| Gửi điểm xong `/top` không đổi | đúng thiết kế, bảng dựng theo cron | dev: cron dày; production: chờ tối đa 20 phút |
| Hai môi trường đọc bảng của nhau | dùng chung Redis, trùng `redisKeyPrefix` | mỗi app / môi trường một prefix |
| Client gọi API ra 404 | `BaseUrl` thiếu `/api/v1` (base URL của SDK BackendClient không có phần này) | `BaseUrl = sdkBaseUrl + "/api/v1"` |
| Panel chạy nhưng không có chữ | project chưa import TMP Essential Resources, hoặc font thiếu dấu tiếng Việt | import TMP Essential Resources; truyền font của game vào `CreatePanel(parent, font)` |
| Unity bỏ qua file trong package cài từ git | file mới thiếu `.meta` | luôn commit `.meta` cùng file (mục 6) |

## 5. Vận hành sau khi lên production

- **Log cần theo dõi** (tên logger trong ngoặc):
  - `[LeaderboardScoreFlush] worker flush điểm chạy`: khởi động; chỉ worker job có dòng này.
  - `[LeaderboardMaintenance] top-boards: N board trong X ms` và `histograms: …`: mỗi lượt
    cron. Thời gian tăng dần là dấu hiệu MySQL đang chậm.
  - `bỏ điểm không ghi được`: dòng hỏng vì chính dữ liệu của nó, đã bị bỏ để không kẹt hàng đợi.
  - `ghi lô điểm lỗi, trả … bản về hàng đợi`: lỗi tạm thời, sẽ thử lại; lặp liên tục thì xem
    MySQL.
- **Redis:**
  - Key dạng `<prefix>v1:…`.
  - Độ dài hàng đợi: `HLEN <prefix>v1:score-pending`. Bình thường gần 0; tăng mãi là worker không
    chạy hoặc MySQL nghẽn.
  - Không key nào cần SCAN.
- **Chặn gian lận:**
  - Gọi `blockUid(uid, lý do)`: cấm submit, đưa điểm về 0, `/rank` trả null ngay; uid biến mất
    khỏi bảng ở lượt dựng kế tiếp, muốn ngay thì gọi `refreshNow()`.
  - Insert tay vào `leaderboard_blocked_uids` cũng được (điền `id = UUID()`). Bảng lọc uid đó ở
    lượt dựng kế tiếp; submit và `/rank` bị chặn sau tối đa 60 giây. Nhưng điểm cũ vẫn được tính
    khi đếm hạng của người khác cho tới khi đưa về 0, nên dùng `blockUid`.
- **`refreshNow()`:** ghi hết hàng đợi rồi dựng lại mọi bảng. Nặng — chỉ gọi từ trang admin, đừng
  gắn vào request của người chơi.
- **Deploy cuốn chiếu:** cron không chạy lúc khởi động, nên khởi động lại nhiều container cùng lúc
  không dồn tải lên MySQL. Điểm đang dở trong worker cũ được worker mới gom lại (log
  `gom lại N điểm dở của worker cũ`).

## 6. Phát triển và phát hành bản mới

**Test:**

| Phần | Lệnh | Ghi chú |
|---|---|---|
| Server unit | `cd server && npm ci && npm test` | Node 20 (đã chạy cả Node 18) |
| Server integration | xem [`server/test/integration/README.md`](server/test/integration/README.md) | dựng 2 container MySQL + Redis dùng một lần; test chạy `FLUSHDB` nên trỏ vào DB Redis riêng (`redis://…/5`) nếu cạnh app khác |
| Unity, nhanh | `python3 unity/tools/compile_check.py --project <project Unity có UniTask> --out <tmp> Runtime Editor Tests Samples~` | biên dịch bằng `dotnet`, vài giây |
| Unity, đầy đủ | `unity/tools/run_unity_tests.sh <tmp> EditMode` (và `PlayMode`) | dựng project tạm, lấy UniTask từ `Library/PackageCache` của một project có sẵn (mặc định AntFood — truyền tham số thứ 4 nếu máy bạn không có) |

**Phát hành:**
1. Sửa code kèm test, cập nhật `CHANGELOG.md` của phần đó.
2. Tăng version:
   - Server: `server/package.json`.
   - Unity: `unity/com.abigames.leaderboard/package.json`, kèm tag trong README Unity
     (mục *Cài đặt*).
3. **Unity — `.meta`:** mọi file mới trong package phải có `.meta` đi kèm. Mở package bằng một
   project trỏ `file:` vào thư mục package để Unity tự sinh, rồi commit. Thiếu `.meta` thì project
   cài qua git sẽ bỏ qua file đó.
4. **Server — tarball:** `npm run pack:release` → `abigames-leaderboard-server-<version>.tgz`
   (gitignore). Tarball có kèm README và CHANGELOG, nên đóng gói **sau** khi sửa tài liệu.
5. **Tag theo từng phần:** repo chứa hai package nên tag có tiền tố, ví dụ `server-v0.1.1` và
   `unity-v0.2.0`. URL Unity:
   `https://github.com/AnKoCare/abi-leaderboard.git?path=/unity/com.abigames.leaderboard#unity-v0.2.0`.
6. Game đang dùng bản cũ: server thì chép tarball mới vào `vendor/` rồi `npm install`; Unity thì
   đổi tag trong `manifest.json`.

**Đổi schema:** thêm migration mới (mốc lớn hơn `1790000000000`), viết theo kiểu "có rồi thì
thôi" như migration hiện có. Đừng sửa migration đã phát hành.

## 7. Giới hạn và việc còn mở

- **Chưa đo tải riêng cho package** (`/top` khi cache hit, `/rank` đường histogram trên bảng
  hàng triệu dòng). Nên đo trên máy test trước khi một game lớn dùng.
- **Cài từ git:** package Unity đã cài thử từ `github.com/AnKoCare/abi-leaderboard` vào project
  Unity 6 trống (không lỗi, không warning, đủ `.meta`, test của package 47 + 9 qua ngay trên bản
  cài từ git). Server đã clone sạch rồi build bằng `npm ci` + `npm run pack:release`, smoke test
  qua trên template.
- **Chưa cuộn thử UI trên máy Android thật**, chưa ghép vào UI của game nào.
- **Histogram theo từng mức điểm:** chỉ hợp với điểm có ít giá trị (Level). Điểm dải rộng cần
  histogram theo khoảng — chưa có.
- **Hạng xấp xỉ:** hạng của người vừa lên một mức điểm đã có trong histogram là số xấp xỉ cho tới
  lượt dựng histogram sau (tối đa 15 phút).
- **Nút cuộn của panel mẫu:** ▲ ● ▼ đè lên cột điểm khi đang lướt (rồi tự mờ). Panel mẫu là điểm
  khởi đầu, game nên chỉnh lại.
- **Template game-backend có sẵn 2 lỗi BXH**, Colony Flow production cũng có; cách nối ở mục 3 đã
  tránh được cả hai:
  - hàng đợi info nuốt Level cao khi lưu liền nhau;
  - người chơi mới chọn quốc gia trước khi có điểm sẽ vắng mặt ở bảng quốc gia.

## 8. Nguồn gốc và quyết định thiết kế

Tách từ module `src/modules/leaderboard` của Colony Flow (commit `e241f8c` trên production) và
bảng xếp hạng trong client AntFood.

**Quyết định đã chốt (22/10/2026):**
- Chỉ làm module NestJS cho host giống template, kèm adapter đọc thẳng bảng `players`.
- Điểm = Level.
- Không làm BXH Team.
- Client core không phụ thuộc SDK BackendClient.
- Điểm phụ tổng quát hoá từ `leaguePoint` / `leagueCycle` thành `secondaryScore` / mùa, mặc định
  tắt; cột DB vẫn giữ tên cũ để dùng lại bảng của template.

**Khác bản gốc của Colony Flow** (đều có test):
- Thứ tự có chốt `id` cuối: bản gốc trả hạng lệch với vị trí trong bảng khi hai dòng trùng hoàn
  toàn.
- Worker bỏ dòng hỏng vì chính dữ liệu của nó: bản gốc trả về hàng đợi mãi (từng gây sự cố
  vòng lặp ghi trên production ngày 14/10/2026).
- Không dùng SCAN.
- Hàng đợi có danh sách worker kèm nhịp tim, worker chết thì worker khác gom lại phần dở.
- Client trả lỗi dưới dạng dữ liệu (`LeaderboardResult<T>`) thay vì ném exception.
- UI không phụ thuộc TW.UGUI / R3 / Odin / Zenject.
