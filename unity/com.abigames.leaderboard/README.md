# AbiGames Leaderboard (Unity)

Client bảng xếp hạng người chơi (World + theo quốc gia) cho server
[`@abigames/leaderboard-server`](../../server/README.md). Tách từ bảng xếp hạng của
Colony Flow, bỏ hết phần dính game (TW.UGUI, R3, Odin, Zenject, hệ thống profile).

**Core** (không UI) — dùng được cả khi game tự vẽ bảng:

| Lớp | Làm gì |
|---|---|
| `LeaderboardClient` | Gọi `submit` / `top` / `rank`. Lỗi trả về như dữ liệu (`LeaderboardResult<T>`), 401 thì làm mới token rồi thử lại một lần |
| `LeaderboardBoardLoader` | Mở một board: hiện cache → trang đầu 100 dòng → tải nền tới 1.000 dòng → "hạng của tôi" → ghi cache. Mở lại trong 5 phút không gọi mạng |
| `LeaderboardFileCache` | Cache mỗi board một file trong `persistentDataPath`, đọc/ghi trên thread pool |
| `LeaderboardScoreSubmitter` | Gửi điểm "bắn rồi quên": gộp, chỉ gửi số lớn nhất, thử lại khi lỗi tạm thời, giữ điểm qua lần tắt game |
| `Testing.FakeLeaderboardTransport` | Server giả trong bộ nhớ — test, demo, dựng UI khi chưa có backend |

**UI** (uGUI + TextMeshPro, assembly `AbiGames.Leaderboard.UI`) — xem [mục UI](#ui).

## Cài đặt

Package cần **UniTask** — thêm vào `Packages/manifest.json` nếu project chưa có (project
dùng khung BaseGame / SDK BackendClient đã có sẵn):

```json
"com.cysharp.unitask": "https://github.com/Cysharp/UniTask.git?path=src/UniTask/Assets/Plugins/UniTask#2.5.11",
"com.abigames.leaderboard": "https://github.com/<org>/abi-leaderboard.git?path=/unity/com.abigames.leaderboard#unity-v0.2.0"
```

Chưa có repo git thì trỏ thẳng vào thư mục package (đường dẫn tuyệt đối, hoặc tương đối so
với thư mục `Packages/`):

```json
"com.abigames.leaderboard": "file:../../abi-leaderboard/unity/com.abigames.leaderboard"
```

`com.unity.nuget.newtonsoft-json` và `com.unity.ugui` tự được kéo về. Cần **Unity 6** (đã chạy
test trên 6000.3). Sau khi Unity cài xong:

1. UI dùng TextMeshPro: *Window → TextMeshPro → Import TMP Essential Resources* (project mới
   chưa có). Thiếu bước này panel vẫn chạy nhưng chữ không hiện.
2. *Window → Package Manager → AbiGames Leaderboard → Samples*: import **UI Demo**, tạo scene
   trống, gắn `LeaderboardUIDemo` vào một GameObject rồi bấm Play — chạy với server giả, không
   cần backend.

Đã thử đúng các bước trên trong một project Unity 6 trống (chỉ có module mặc định): cài
không lỗi, không warning, sample và menu *Create Sample Panel* chạy được.

## Dùng nhanh

```csharp
using AbiGames.Leaderboard;

LeaderboardConfig config = new LeaderboardConfig
{
    BaseUrl = "https://api.game.com/api/v1",               // GỒM global prefix của server
    AccessTokenProvider = () => myAuth.AccessToken,        // null = chưa đăng nhập
    RefreshAccessToken = () => myAuth.RefreshAsync(),      // tuỳ chọn: UniTask<bool>
    SelfUidProvider = () => myProfile.Uid,                 // uid công khai của người chơi
    StorageKey = "prod",                                    // tách cache giữa dev / prod
};

LeaderboardClient client = new LeaderboardClient(config);
LeaderboardBoardLoader loader = new LeaderboardBoardLoader(client, LeaderboardFileCache.FromConfig(config));
LeaderboardScoreSubmitter submitter = new LeaderboardScoreSubmitter(client);
```

**Mở bảng** — UI vẽ lại mỗi lần có snapshot mới:

```csharp
CancellationTokenSource cts;   // huỷ khi đổi tab / đóng bảng

async UniTaskVoid OpenBoard(string board)
{
    cts?.Cancel();
    cts = CancellationTokenSource.CreateLinkedTokenSource(this.GetCancellationTokenOnDestroy());
    try
    {
        await loader.LoadAsync(board, Render, cancellationToken: cts.Token);
    }
    catch (OperationCanceledException) { }
}

void Render(LeaderboardSnapshot s)
{
    myList.Show(s.Entries);                                        // danh sách của game; tối đa 1.000 dòng
    loading.SetActive(s.Entries.Count == 0 && s.Stage != LeaderboardLoadStage.Failed);
    staleLabel.text = s.IsFromCache ? $"Dữ liệu lúc {s.CachedAtLocalTime:HH:mm dd/MM}" : "";
    myRankCell.gameObject.SetActive(s.SelfRank != null);          // null = không có trên board
}
```

`board` là `LeaderboardBoards.World` (`"ww"`) hoặc mã quốc gia viết thường (`"vn"`).

**Gửi điểm** khi thắng level — không cần chờ:

```csharp
submitter.Submit(currentLevel);

// sau khi đăng nhập xong / app quay lại foreground:
submitter.ResendPending();
```

**Đọc profile của một dòng** — `info` chỉ có các key server khai báo (template: Level,
PlayerName, AvatarIndex, FrameIndex, BadgeIndex):

```csharp
string name = entry.GetInfoString("PlayerName", entry.Uid);
int avatar = entry.GetInfoInt("AvatarIndex", 0);
MyProfileInfo info = entry.InfoAs<MyProfileInfo>();               // hoặc đọc thành class của game
```

## UI

| Component | Làm gì |
|---|---|
| `LeaderboardBoardView` | Một bảng: danh sách + ô "hạng của tôi" ghim dưới + trạng thái đang tải / trống / lỗi / "dữ liệu lúc ...". Tự tải khi bật, huỷ khi tắt |
| `LeaderboardVirtualList` | Danh sách cuộn ảo đặt trên `Content` của ScrollRect: 1.000 dòng chỉ dựng ~15 ô. Có `ScrollToIndex` |
| `LeaderboardCellBase` | Ô gốc — game kế thừa và override `Render(entry, isSelf)` để vẽ theo phong cách riêng |
| `DefaultLeaderboardCell` | Ô có sẵn: hạng (màu vàng / bạc / đồng cho top 3), avatar, tên, cờ hoặc mã quốc gia, điểm, điểm phụ |
| `LeaderboardTabsView` | Tab World / Country / board cố định, có điều kiện khoá (vd. chưa đủ level) |
| `LeaderboardScrollJumpButtons` | Nút lên đầu / tới hạng của tôi / xuống cuối; hiện khi lướt, tự mờ |
| `LeaderboardAvatarSet`, `LeaderboardCountryFlagSet` | ScriptableObject: sprite avatar theo chỉ số, cờ theo mã quốc gia |
| `LeaderboardPanelFactory` | Dựng cả panel bằng code (menu, demo, test dùng chung) |

**Bắt đầu nhanh:** *Tools → AbiGames → Leaderboard → Create Sample Panel* dựng panel vào scene
đang mở; kéo vào Project để thành prefab rồi chỉnh màu / ô theo game. Dựng bằng code thì
`LeaderboardPanelFactory.CreatePanel(parent, font)` — truyền font của game nếu font mặc định của
TMP Settings thiếu ký tự (tiếng Việt...). Lúc boot gọi
`LeaderboardRuntime.Initialize(config)` — panel bật lên là tự tải:

```csharp
LeaderboardRuntime.Initialize(config);                       // một lần, sau khi biết base URL

tabs.PlayerCountryProvider = () => PlayerData.Instance.CountryCode;
tabs.IsTabLocked = tab => PlayerData.Instance.CurrentLevel.Value < 10;
foreach (LeaderboardTab tab in tabs.Tabs)
{
    tab.View.SelfProfileProvider = () => JObject.FromObject(new { PlayerName = myName, AvatarIndex = myAvatar });
    tab.View.EntryClicked += entry => OpenProfile(entry.Uid);
}

LeaderboardRuntime.Submitter.Submit(currentLevel);           // khi thắng level
```

- `SelfProfileProvider` dùng cho ô "hạng của tôi" khi người chơi nằm **ngoài top** — lúc đó
  server chỉ trả hạng, không trả profile.
- **Ô riêng của game:** kế thừa `LeaderboardCellBase`, override `Render`, gán prefab ô đó vào
  `CellTemplate` (ô mẫu đặt NGOÀI `Content`, để tắt) và `SelfCell` của `LeaderboardBoardView`.
  `Render` phải vẽ lại toàn bộ ô — một ô được dùng lại cho nhiều dòng khi cuộn.
- **Trong popup của khung TW.UGUI:** đặt panel vào prefab modal. Tab tự mở khi modal bật
  (`OpenOnEnable`); muốn chủ động thì tắt `OpenOnEnable` rồi gọi `tabs.Select(0)` trong `DidPushEnter`.
- **Game có DI riêng:** bỏ qua `LeaderboardRuntime`, gọi `view.Bind(loader)` cho từng bảng.

## Dùng chung với SDK BackendClient

SDK đã giữ token và base URL. Lưu ý `baseUrl` của SDK **không** gồm `/api/v1`:

```csharp
IGameBackendManager backend = …;                                   // inject như mọi service khác của SDK
LeaderboardConfig config = new LeaderboardConfig
{
    BaseUrl = sdkBaseUrl.TrimEnd('/') + "/api/v1",
    AccessTokenProvider = () => backend.IsAuthenticated ? backend.AuthToken : null,
    SelfUidProvider = () => PlayerData.Instance.PlayerIdServer,
};
```

## Lỗi

`LeaderboardResult<T>.Error.Kind`:

| Kind | Khi nào | Thử lại được |
|---|---|---|
| `Network`, `Timeout` | mất mạng, quá `TimeoutSeconds` | có |
| `RateLimited` | 429, hoặc 503 do nginx chặn `/rank` | có |
| `Server` | 5xx khác | có |
| `Unauthorized` | 401 (đã thử làm mới token mà vẫn hỏng) | sau khi đăng nhập |
| `Forbidden` | 403 — uid bị cấm lên bảng | không |
| `NotFound` | 404 — tài khoản chưa có player | không |
| `BadRequest`, `InvalidArgument`, `Parse` | tham số / dữ liệu sai | không |

Huỷ bằng `CancellationToken` vẫn ném `OperationCanceledException` như mọi API UniTask.

## Ghi chú

- Mọi lớp dùng trên **main thread**; parse JSON trang lớn tự chạy trên thread pool
  (`ParseOnThreadPool`, tự tắt trên WebGL).
- Bảng trên server chỉ dựng lại khoảng 20 phút một lần và hạng cache 1 giờ, nên điểm vừa
  gửi chưa lên bảng ngay. Đó là thiết kế, không phải lỗi.
- Đổi tài khoản / đổi tên hiển thị thì gọi `cache.ClearAll()` và `loader.Invalidate()`.
- Server đặt `routePrefix` khác `leaderboard` thì đặt `config.RoutePrefix` cho khớp.

## Mẫu

*Package Manager → AbiGames Leaderboard → Samples*:
- **Offline Demo**: gắn `LeaderboardOfflineDemo` vào một GameObject rồi bấm Play. Bản demo dựng
  server giả 1.500 người, tải board World và VN, gửi điểm, rồi in kết quả ra Console.
- **UI Demo**: gắn `LeaderboardUIDemo` vào một GameObject trong scene trống rồi bấm Play. Bản demo
  dựng panel, chạy với server giả; vài giây sau gửi một điểm cao hơn để thấy hạng nhảy.

## Test

Có test EditMode (`Tests/Editor`, phần core) và PlayMode (`Tests/Runtime`, phần UI). Thêm
`"testables": ["com.abigames.leaderboard"]` vào manifest rồi chạy trong *Window → General → Test Runner*.

Không muốn đụng project game, dùng script dựng project tạm rồi chạy test bằng batchmode:

```bash
unity/tools/run_unity_tests.sh /tmp/lb-test EditMode
```

Còn `unity/tools/compile_check.py` biên dịch nhanh bằng `dotnet` để bắt lỗi C# trong vài giây.
