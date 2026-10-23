using AbiGames.Leaderboard.Testing;
using AbiGames.Leaderboard.UI;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;

namespace AbiGames.Leaderboard.Samples
{
    /// <summary>
    /// Gắn vào một GameObject trong scene trống rồi bấm Play: dựng server giả, dựng panel bằng
    /// <see cref="LeaderboardPanelFactory"/> (hoặc dùng panel có sẵn gán vào <see cref="tabs"/>),
    /// mở tab World. Sau vài giây gửi một điểm cao hơn và tải lại để thấy hạng nhảy.
    ///
    /// Cần TMP Essential Resources (Window → TextMeshPro → Import TMP Essential Resources).
    /// </summary>
    public class LeaderboardUIDemo : MonoBehaviour
    {
        [Tooltip("Panel có sẵn trong scene. Để trống thì tự dựng.")]
        [SerializeField] private LeaderboardTabsView tabs;
        [Tooltip("Font cho panel tự dựng. Để trống = font mặc định của TMP Settings.")]
        [SerializeField] private TMP_FontAsset font = null;
        [SerializeField] private int playerCount = 1500;
        [SerializeField] private float fakeLatencySeconds = 0.4f;
        [SerializeField] private string myCountry = "vn";
        [SerializeField] private int myLevel = 300;
        [SerializeField] private float submitAfterSeconds = 4f;
        [SerializeField] private int newLevel = 1750;

        private const string MyUid = "1000000777";
        private const string MyToken = "demo-token";

        private void Start()
        {
            FakeLeaderboardTransport server = new FakeLeaderboardTransport { LatencySeconds = fakeLatencySeconds };
            string[] countries = { "vn", "us", "jp", "br", "id" };
            for (int i = 1; i <= playerCount; i++)
            {
                int level = Random.Range(1, 1800);
                server.AddPlayer((1_000_000_000L + i).ToString(), level, countries[i % countries.Length],
                    new { PlayerName = "Player " + i, Level = level, AvatarIndex = i % 5 });
            }
            server.AddPlayer(MyUid, myLevel, myCountry, new { PlayerName = "Me", Level = myLevel, AvatarIndex = 2 });
            server.SetToken(MyToken, MyUid);

            LeaderboardRuntime.Initialize(new LeaderboardConfig
            {
                BaseUrl = "https://offline.demo/api/v1",
                AccessTokenProvider = () => MyToken,
                SelfUidProvider = () => MyUid,
                StorageKey = "ui-demo",
                CacheFolder = null,
            }, server);

            if (tabs == null)
                tabs = LeaderboardPanelFactory.CreatePanel(LeaderboardPanelFactory.CreateCanvas().transform, font).Tabs;

            tabs.PlayerCountryProvider = () => myCountry;
            foreach (LeaderboardTab tab in tabs.Tabs)
            {
                if (tab.View == null)
                    continue;
                tab.View.SelfProfileProvider = () => JObject.FromObject(new { PlayerName = "Me", Level = myLevel, AvatarIndex = 2 });
                tab.View.EntryClicked += entry => Debug.Log($"[UIDemo] bấm #{entry.Rank} {entry.GetInfoString("PlayerName")} ({entry.Uid})");
            }
            tabs.Select(0);

            SubmitLaterAsync().Forget();
        }

        private async UniTaskVoid SubmitLaterAsync()
        {
            await UniTask.Delay(System.TimeSpan.FromSeconds(submitAfterSeconds), cancellationToken: this.GetCancellationTokenOnDestroy());
            myLevel = newLevel;
            LeaderboardRuntime.Submitter.Submit(newLevel);
            await UniTask.WaitUntil(() => !LeaderboardRuntime.Submitter.IsSending, cancellationToken: this.GetCancellationTokenOnDestroy());
            Debug.Log($"[UIDemo] đã gửi level {newLevel} — tải lại bảng");
            LeaderboardTab selected = tabs.Selected;
            if (selected?.View != null)
                selected.View.Refresh(force: true);
        }

        private void OnDestroy()
        {
            LeaderboardRuntime.Shutdown();
        }
    }
}
