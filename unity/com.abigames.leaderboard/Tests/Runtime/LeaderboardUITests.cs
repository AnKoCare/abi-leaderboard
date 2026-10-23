using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using AbiGames.Leaderboard.Testing;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace AbiGames.Leaderboard.UI.Tests
{
    public class LeaderboardUITests
    {
        private const string Token = "tok";
        private Canvas canvas;
        private FakeLeaderboardTransport fake;
        private static TMPro.TMP_FontAsset font;

        [SetUp]
        public void SetUp()
        {
            canvas = LeaderboardPanelFactory.CreateCanvas("TestCanvas");
            fake = new FakeLeaderboardTransport();
            // Font dựng lúc chạy từ font có sẵn của Unity — project test không có font mặc định của TMP.
            // Project test không có font mặc định của TMP, và font có sẵn của Unity là font động không
            // kèm dữ liệu (CreateFontAsset trả null) → dựng từ font của hệ điều hành. Không có thì font
            // = null: các test khác vẫn chạy, riêng test font báo inconclusive thay vì qua oan.
            if (font == null)
                font = CreateSystemFont();
        }

        [TearDown]
        public void TearDown()
        {
            LeaderboardRuntime.Shutdown();
            if (canvas != null)
                Object.Destroy(canvas.gameObject);
            EventSystem es = Object.FindFirstObjectByType<EventSystem>();
            if (es != null)
                Object.Destroy(es.gameObject);
        }

        private static string Uid(int n) => (1_000_000_000L + n).ToString();

        /// <summary><paramref name="count"/> người điểm giảm dần (Uid(1) đứng đầu), xen kẽ vn / us.</summary>
        private void Seed(int count)
        {
            for (int i = 1; i <= count; i++)
                fake.AddPlayer(Uid(i), 100_000 - i, i % 2 == 0 ? "us" : "vn", new { PlayerName = "P" + i, Level = 100_000 - i });
        }

        private void InitRuntime(string selfUid)
        {
            fake.SetToken(Token, selfUid);
            LeaderboardRuntime.Initialize(new LeaderboardConfig
            {
                BaseUrl = "https://fake.test/api/v1",
                AccessTokenProvider = () => Token,
                SelfUidProvider = () => selfUid,
                CacheFolder = null,
                ParseOnThreadPool = false,
                StorageKey = "ui-test",
            }, fake);
        }

        private LeaderboardPanelFactory.Panel CreatePanel() => LeaderboardPanelFactory.CreatePanel(canvas.transform, font);

        private static TMPro.TMP_FontAsset CreateSystemFont()
        {
            foreach (string family in new[] { "Arial", "Helvetica", "Helvetica Neue", "Liberation Sans", "DejaVu Sans", "Roboto", "Segoe UI" })
            {
                TMPro.TMP_FontAsset asset = TMPro.TMP_FontAsset.CreateFontAsset(family, "Regular");
                if (asset != null)
                    return asset;
            }
            return null;
        }

        private static async UniTask WaitUntil(Func<bool> condition, float timeoutSeconds = 5f)
        {
            using (CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
            {
                try
                {
                    await UniTask.WaitUntil(condition, cancellationToken: cts.Token);
                }
                catch (OperationCanceledException)
                {
                    Assert.Fail($"quá {timeoutSeconds}s mà điều kiện chưa đúng");
                }
            }
        }

        private static UniTask WaitComplete(LeaderboardBoardView view) =>
            WaitUntil(() => view.Current != null &&
                            (view.Current.Stage == LeaderboardLoadStage.Complete || view.Current.Stage == LeaderboardLoadStage.Failed));

        private static List<LeaderboardCellBase> ActiveCells(LeaderboardVirtualList list) =>
            list.GetComponentsInChildren<LeaderboardCellBase>(false).ToList();

        [UnityTest]
        public IEnumerator DanhSachAo_1000Dong_ChiDungVaiChucO_DatDungViTri() => UniTask.ToCoroutine(async () =>
        {
            Seed(1500);
            InitRuntime(Uid(1));
            LeaderboardPanelFactory.Panel panel = CreatePanel();
            await WaitComplete(panel.WorldView);
            await UniTask.Yield();

            LeaderboardVirtualList list = panel.WorldView.List;
            Assert.AreEqual(1000, list.Count);
            float viewportHeight = list.ScrollRect.viewport.rect.height;
            float pitch = list.CellHeight + list.Spacing;
            int expectedPool = Mathf.CeilToInt(viewportHeight / pitch) + list.BufferCells * 2 + 1;
            Assert.AreEqual(expectedPool, list.PooledCellCount, $"viewport {viewportHeight}px");
            Assert.Less(list.PooledCellCount, 40);

            RectTransform content = (RectTransform)list.transform;
            Assert.AreEqual(list.PaddingTop + 1000 * pitch - list.Spacing + list.PaddingBottom, content.sizeDelta.y, 0.01f);

            list.ScrollToIndex(500, center: false);
            await UniTask.Yield();
            List<LeaderboardCellBase> active = ActiveCells(list);
            Assert.IsTrue(active.Any(c => c.Entry.Rank == 501), "dòng 501 phải đang hiện ở đỉnh");
            foreach (LeaderboardCellBase cell in active)
            {
                float expectedTop = list.PaddingTop + (cell.Entry.Rank - 1) * pitch;
                Assert.AreEqual(-expectedTop, ((RectTransform)cell.transform).offsetMax.y, 0.01f, $"ô hạng {cell.Entry.Rank} lệch chỗ");
            }
        });

        private sealed class CountingCell : LeaderboardCellBase
        {
            public int Renders;
            protected override void Render(LeaderboardEntry entry, bool isSelf) => Renders += 1;
        }

        [UnityTest]
        public IEnumerator NoiThemDuLieu_KhongVeLaiOdangHien() => UniTask.ToCoroutine(async () =>
        {
            GameObject scrollGo = new GameObject("Scroll", typeof(RectTransform), typeof(ScrollRect));
            scrollGo.transform.SetParent(canvas.transform, false);
            RectTransform scrollRect = (RectTransform)scrollGo.transform;
            scrollRect.anchorMin = Vector2.zero;
            scrollRect.anchorMax = Vector2.one;
            scrollRect.offsetMin = scrollRect.offsetMax = Vector2.zero;
            GameObject viewportGo = new GameObject("Viewport", typeof(RectTransform), typeof(RectMask2D));
            viewportGo.transform.SetParent(scrollRect, false);
            RectTransform viewport = (RectTransform)viewportGo.transform;
            viewport.anchorMin = Vector2.zero;
            viewport.anchorMax = Vector2.one;
            viewport.offsetMin = viewport.offsetMax = Vector2.zero;
            GameObject contentGo = new GameObject("Content", typeof(RectTransform));
            contentGo.transform.SetParent(viewport, false);
            RectTransform content = (RectTransform)contentGo.transform;
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            ScrollRect scroll = scrollGo.GetComponent<ScrollRect>();
            scroll.viewport = viewport;
            scroll.content = content;
            LeaderboardVirtualList list = contentGo.AddComponent<LeaderboardVirtualList>();
            GameObject templateGo = new GameObject("Template", typeof(RectTransform));
            templateGo.transform.SetParent(scrollRect, false);
            CountingCell template = templateGo.AddComponent<CountingCell>();
            templateGo.SetActive(false);

            list.Initialize(template);
            List<LeaderboardEntry> first = Enumerable.Range(1, 100).Select(i => new LeaderboardEntry { Rank = i, Uid = Uid(i) }).ToList();
            list.SetItems(first, null);
            await UniTask.Yield();
            int Renders() => contentGo.GetComponentsInChildren<CountingCell>(true).Sum(c => c.Renders);
            int afterFirst = Renders();
            Assert.Greater(afterFirst, 0);

            List<LeaderboardEntry> all = first.Concat(Enumerable.Range(101, 900).Select(i => new LeaderboardEntry { Rank = i, Uid = Uid(i) })).ToList();
            list.SetItems(all, null);
            await UniTask.Yield();
            Assert.AreEqual(1000, list.Count);
            Assert.AreEqual(afterFirst, Renders(), "nối thêm phía sau không được vẽ lại các ô đang hiện");

            list.SetItems(all.Select(e => new LeaderboardEntry { Rank = e.Rank, Uid = e.Uid }).ToList(), null);
            await UniTask.Yield();
            Assert.Greater(Renders(), afterFirst, "dữ liệu mới hẳn thì phải vẽ lại");
        });

        [UnityTest]
        public IEnumerator HaiTab_OHangCuaToiNgoaiTop_DungProfileCuaMay() => UniTask.ToCoroutine(async () =>
        {
            Seed(1500);
            InitRuntime(Uid(1201));
            LeaderboardPanelFactory.Panel panel = CreatePanel();
            panel.Tabs.PlayerCountryProvider = () => "VN";
            panel.WorldView.SelfProfileProvider = () => JObject.FromObject(new { PlayerName = "Tôi" });
            panel.Tabs.Select(0);
            await WaitComplete(panel.WorldView);

            Assert.AreEqual(1000, panel.WorldView.List.Count);
            Assert.IsTrue(panel.WorldView.SelfCell.gameObject.activeSelf);
            Assert.AreEqual(1201, panel.WorldView.SelfCell.Entry.Rank);
            Assert.AreEqual("Tôi", panel.WorldView.SelfCell.Entry.GetInfoString("PlayerName"));
            Assert.IsFalse(panel.WorldView.LoadingRoot.activeSelf);
            Assert.IsFalse(panel.WorldView.EmptyRoot.activeSelf);

            panel.Tabs.Select(1);
            await WaitComplete(panel.CountryView);
            Assert.AreEqual("vn", panel.CountryView.Board);
            Assert.IsFalse(panel.WorldView.gameObject.activeSelf);
            Assert.IsTrue(panel.CountryView.List.Items.All(e => e.CountryCode == "vn"));
            Assert.AreEqual(601, panel.CountryView.SelfCell.Entry.Rank, "Uid(1201) là người vn thứ 601");
        });

        [UnityTest]
        public IEnumerator TabBiKhoa_KhongGoiMang() => UniTask.ToCoroutine(async () =>
        {
            Seed(50);
            InitRuntime(Uid(1));
            LeaderboardPanelFactory.Panel panel = CreatePanel();
            panel.Tabs.IsTabLocked = tab => tab.Kind == LeaderboardTabKind.PlayerCountry;
            await WaitComplete(panel.WorldView);
            int before = fake.Requests.Count;

            panel.Tabs.Select(1);
            await UniTask.Yield();
            Assert.IsTrue(panel.Tabs.Tabs[1].LockedRoot.activeSelf);
            Assert.IsFalse(panel.CountryView.gameObject.activeSelf);
            Assert.AreEqual(before, fake.Requests.Count);
        });

        [UnityTest]
        public IEnumerator BoardTrong_HienTrong_KhongHienHangCuaToi() => UniTask.ToCoroutine(async () =>
        {
            Seed(20);
            InitRuntime(Uid(1));
            LeaderboardPanelFactory.Panel panel = CreatePanel();
            panel.Tabs.PlayerCountryProvider = () => "jp";
            panel.Tabs.Select(1);
            await WaitComplete(panel.CountryView);
            Assert.IsTrue(panel.CountryView.EmptyRoot.activeSelf);
            Assert.IsFalse(panel.CountryView.SelfCell.gameObject.activeSelf);
            Assert.IsFalse(panel.CountryView.LoadingRoot.activeSelf);
        });

        [UnityTest]
        public IEnumerator MatMangKhongCoCache_HienLoi() => UniTask.ToCoroutine(async () =>
        {
            Seed(20);
            InitRuntime(Uid(1));
            fake.EnqueueResponse(0, null, networkError: true);
            LeaderboardPanelFactory.Panel panel = CreatePanel();
            await WaitComplete(panel.WorldView);
            Assert.AreEqual(LeaderboardLoadStage.Failed, panel.WorldView.Current.Stage);
            Assert.IsTrue(panel.WorldView.ErrorRoot.activeSelf);
            Assert.IsFalse(panel.WorldView.LoadingRoot.activeSelf);
        });

        [UnityTest]
        public IEnumerator BamMotDong_BaoRaNgoai() => UniTask.ToCoroutine(async () =>
        {
            Seed(30);
            InitRuntime(Uid(1));
            LeaderboardPanelFactory.Panel panel = CreatePanel();
            LeaderboardEntry clicked = null;
            panel.WorldView.EntryClicked += e => clicked = e;
            await WaitComplete(panel.WorldView);
            await UniTask.Yield();

            DefaultLeaderboardCell cell = panel.WorldView.List.GetComponentsInChildren<DefaultLeaderboardCell>(false)
                .First(c => c.Entry.Rank == 3);
            cell.Button.onClick.Invoke();
            Assert.AreEqual(Uid(3), clicked?.Uid);
            Assert.AreEqual("P3", cell.NameText.text);
        });

        [UnityTest]
        public IEnumerator FontTruyenVaoFactory_ApChoMoiChu_KeCaONhanBan() => UniTask.ToCoroutine(async () =>
        {
            if (font == null)
                Assert.Inconclusive("máy không có font hệ thống để dựng TMP_FontAsset");
            Seed(30);
            InitRuntime(Uid(1));
            LeaderboardPanelFactory.Panel panel = CreatePanel();
            await WaitComplete(panel.WorldView);
            await UniTask.Yield();

            List<TMPro.TMP_Text> texts = panel.Root.GetComponentsInChildren<TMPro.TMP_Text>(true).ToList();
            List<DefaultLeaderboardCell> clones = panel.WorldView.List.GetComponentsInChildren<DefaultLeaderboardCell>(false).ToList();
            Assert.Greater(clones.Count, 0);
            Assert.IsTrue(texts.All(t => t.font == font), string.Join(", ", texts.Where(t => t.font != font).Select(t => t.name)));
            Assert.IsTrue(clones.All(c => c.NameText.font == font), "ô nhân bản từ ô mẫu phải giữ font");
        });

        [UnityTest]
        public IEnumerator NutToiHangCuaToi_CuonToiDongCuaMinh() => UniTask.ToCoroutine(async () =>
        {
            Seed(1000);
            InitRuntime(Uid(700));
            LeaderboardPanelFactory.Panel panel = CreatePanel();
            await WaitComplete(panel.WorldView);
            await UniTask.Yield();
            Assert.IsFalse(ActiveCells(panel.WorldView.List).Any(c => c.Entry.Rank == 700));

            panel.JumpButtons.JumpToSelf();
            await UniTask.Yield();
            LeaderboardCellBase mine = ActiveCells(panel.WorldView.List).FirstOrDefault(c => c.Entry.Rank == 700);
            Assert.IsNotNull(mine, "dòng của mình phải hiện sau khi bấm");
            Assert.IsTrue(mine.IsSelf);
        });
    }
}
