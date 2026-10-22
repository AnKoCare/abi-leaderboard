# AbiGames Leaderboard (Unity)

Client bảng xếp hạng người chơi (World + theo quốc gia) cho server
[`@abigames/leaderboard-server`](../../server/README.md). Tách từ bảng xếp hạng của
Colony Flow, bỏ hết phần dính game (TW.UGUI, R3, Odin, Zenject, hệ thống profile).

Bản 0.1.0 gồm phần **core** (không UI):

| Lớp | Làm gì |
|---|---|
| `LeaderboardClient` | Gọi `submit` / `top` / `rank`. Lỗi trả về như dữ liệu (`LeaderboardResult<T>`), 401 thì làm mới token rồi thử lại một lần |
| `LeaderboardBoardLoader` | Mở một board: hiện cache → trang đầu 100 dòng → tải nền tới 1.000 dòng → "hạng của tôi" → ghi cache. Mở lại trong 5 phút không gọi mạng |
| `LeaderboardFileCache` | Cache mỗi board một file trong `persistentDataPath`, đọc/ghi trên thread pool |
| `LeaderboardScoreSubmitter` | Gửi điểm "bắn rồi quên": gộp, chỉ gửi số lớn nhất, thử lại khi lỗi tạm thời, giữ điểm qua lần tắt game |
| `Testing.FakeLeaderboardTransport` | Server giả trong bộ nhớ — test, demo, dựng UI khi chưa có backend |

UI (danh sách cuộn ảo, ô mặc định, tab World / Country) sẽ có ở bản sau.

## Cài đặt

Package cần **UniTask** — thêm vào `Packages/manifest.json` nếu project chưa có (project
dùng khung BaseGame / SDK BackendClient đã có sẵn):

```json
"com.cysharp.unitask": "https://github.com/Cysharp/UniTask.git?path=src/UniTask/Assets/Plugins/UniTask",
"com.abigames.leaderboard": "https://github.com/<org>/abi-leaderboard.git?path=/unity/com.abigames.leaderboard#v0.1.0"
```

`com.unity.nuget.newtonsoft-json` tự được kéo về. Cần Unity 2022.3 trở lên (đã chạy test trên 6000.3).

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
    list.SetItems(s.Entries);                                      // tối đa 1.000 dòng
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

*Package Manager → AbiGames Leaderboard → Samples → Offline Demo*: gắn
`LeaderboardOfflineDemo` vào một GameObject rồi bấm Play. Bản demo dựng server giả 1.500
người, tải board World và VN, gửi điểm, rồi in kết quả ra Console.

## Test

Có test EditMode trong `Tests/Editor`. Thêm `"testables": ["com.abigames.leaderboard"]` vào
manifest rồi chạy trong *Window → General → Test Runner*, hoặc chạy bằng dòng lệnh:

```bash
Unity -batchmode -nographics -projectPath <project> -runTests -testPlatform EditMode -testResults results.xml
```
