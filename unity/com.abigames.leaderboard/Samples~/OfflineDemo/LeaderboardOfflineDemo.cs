using AbiGames.Leaderboard.Testing;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace AbiGames.Leaderboard.Samples
{
    /// <summary>
    /// Gắn vào một GameObject bất kỳ trong scene trống rồi bấm Play: dựng server giả
    /// <see cref="playerCount"/> người, tải board World và board VN theo đúng luồng của game
    /// (trang đầu → phần còn lại → hạng của tôi), gửi một điểm mới rồi tải lại. Mọi thứ in ra
    /// Console — không cần backend.
    /// </summary>
    public class LeaderboardOfflineDemo : MonoBehaviour
    {
        [SerializeField] private int playerCount = 1500;
        [SerializeField] private float fakeLatencySeconds = 0.3f;

        private const string MyUid = "1000000777";
        private const string MyToken = "demo-token";

        private void Start()
        {
            RunAsync().Forget();
        }

        private async UniTaskVoid RunAsync()
        {
            FakeLeaderboardTransport server = new FakeLeaderboardTransport { LatencySeconds = fakeLatencySeconds };
            string[] countries = { "vn", "us", "jp", "br" };
            for (int i = 1; i <= playerCount; i++)
            {
                string uid = (1_000_000_000L + i).ToString();
                int level = Random.Range(1, 1800);
                server.AddPlayer(uid, level, countries[i % countries.Length], new { PlayerName = "Player " + i, Level = level });
            }
            server.AddPlayer(MyUid, 120, "vn", new { PlayerName = "Tôi", Level = 120 });
            server.SetToken(MyToken, MyUid);

            LeaderboardConfig config = new LeaderboardConfig
            {
                BaseUrl = "https://offline.demo/api/v1",
                AccessTokenProvider = () => MyToken,
                SelfUidProvider = () => MyUid,
                StorageKey = "offline-demo",
                LogRequests = true,
            };
            LeaderboardClient client = new LeaderboardClient(config, server);
            LeaderboardBoardLoader loader = new LeaderboardBoardLoader(client, LeaderboardFileCache.FromConfig(config));
            LeaderboardScoreSubmitter submitter = new LeaderboardScoreSubmitter(client);

            await Show(loader, LeaderboardBoards.World);
            await Show(loader, "vn");

            submitter.Submitted += r => Debug.Log($"[Demo] server đã nhận điểm {r.Score}");
            submitter.Submit(1750);
            await UniTask.WaitUntil(() => !submitter.IsSending && !submitter.HasPending,
                cancellationToken: this.GetCancellationTokenOnDestroy());

            await Show(loader, LeaderboardBoards.World, force: true);
            submitter.Dispose();
        }

        private async UniTask Show(LeaderboardBoardLoader loader, string board, bool force = false)
        {
            LeaderboardSnapshot result = await loader.LoadAsync(
                board,
                s => Debug.Log($"[Demo] {board}: {s.Stage}, {s.Entries.Count}/{s.BoardTotal} dòng{(s.IsFromCache ? " (cache)" : string.Empty)}"),
                force,
                this.GetCancellationTokenOnDestroy());

            for (int i = 0; i < Mathf.Min(3, result.Entries.Count); i++)
            {
                LeaderboardEntry e = result.Entries[i];
                Debug.Log($"[Demo]   #{e.Rank} {e.GetInfoString("PlayerName")} — level {e.Score} ({e.CountryCode})");
            }
            Debug.Log(result.SelfRank != null
                ? $"[Demo]   hạng của tôi trên {board}: #{result.SelfRank.Rank}/{result.SelfRank.Total}"
                : $"[Demo]   tôi không có trên board {board}");
        }
    }
}
