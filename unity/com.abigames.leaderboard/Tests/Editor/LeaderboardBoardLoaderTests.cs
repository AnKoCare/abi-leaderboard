using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using AbiGames.Leaderboard.Testing;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace AbiGames.Leaderboard.Tests
{
    public class LeaderboardBoardLoaderTests
    {
        private FakeLeaderboardTransport fake;

        [SetUp]
        public void SetUp()
        {
            fake = new FakeLeaderboardTransport();
        }

        private LeaderboardBoardLoader Loader(ILeaderboardCache cache = null, ILeaderboardTransport transport = null, LeaderboardConfig config = null)
        {
            return new LeaderboardBoardLoader(new LeaderboardClient(config ?? TestKit.Config(), transport ?? fake), cache);
        }

        [UnityTest]
        public IEnumerator TaiTrangDauRoiTaiNenPhanConLai() => UniTask.ToCoroutine(async () =>
        {
            TestKit.Seed(fake, 250);
            List<LeaderboardLoadStage> stages = new List<LeaderboardLoadStage>();
            List<int> counts = new List<int>();

            LeaderboardSnapshot result = await Loader().LoadAsync("ww", s =>
            {
                stages.Add(s.Stage);
                counts.Add(s.Entries.Count);
            });

            CollectionAssert.AreEqual(new[] { LeaderboardLoadStage.FirstPage, LeaderboardLoadStage.Complete }, stages);
            CollectionAssert.AreEqual(new[] { 100, 250 }, counts);
            StringAssert.Contains("limit=100&offset=0", fake.Requests[0].Url);
            StringAssert.Contains("limit=150&offset=100", fake.Requests[1].Url);
            CollectionAssert.AreEqual(Enumerable.Range(1, 250).ToArray(), result.Entries.Select(e => e.Rank).ToArray());
            Assert.AreEqual(250, result.BoardTotal);
            Assert.IsFalse(result.IsFromCache);
            Assert.IsFalse(result.IsPartial);
        });

        [UnityTest]
        public IEnumerator BoardDuoiMotTrang_ChiMotRequest() => UniTask.ToCoroutine(async () =>
        {
            TestKit.Seed(fake, 30);
            LeaderboardSnapshot result = await Loader().LoadAsync("vn");
            Assert.AreEqual(30, result.Entries.Count);
            Assert.AreEqual(1, fake.CountRequests("/top"));
        });

        [UnityTest]
        public IEnumerator MinhNamTrongTop_KhongGoiRank() => UniTask.ToCoroutine(async () =>
        {
            TestKit.Seed(fake, 250);
            LeaderboardSnapshot result = await Loader().LoadAsync("ww");
            Assert.AreEqual(0, fake.CountRequests("/rank"));
            Assert.IsTrue(result.SelfRankKnown);
            Assert.AreEqual(1, result.SelfRank.Rank, "SelfUid = Uid(1) đứng đầu");
        });

        [UnityTest]
        public IEnumerator MinhNgoaiTop1000_HoiRank() => UniTask.ToCoroutine(async () =>
        {
            TestKit.Seed(fake, 1200);
            LeaderboardConfig config = TestKit.Config(uid: () => TestKit.Uid(1100));
            LeaderboardSnapshot result = await Loader(config: config).LoadAsync("ww");
            Assert.AreEqual(1000, result.Entries.Count);
            Assert.AreEqual(2, fake.CountRequests("/top"), "100 dòng đầu + 900 dòng còn lại");
            Assert.AreEqual(1, fake.CountRequests("/rank"));
            Assert.AreEqual(1100, result.SelfRank.Rank);
            Assert.AreEqual(1200, result.SelfRank.Total);
        });

        [UnityTest]
        public IEnumerator KhongCoTrenBoardQuocGia_SelfRankNull() => UniTask.ToCoroutine(async () =>
        {
            TestKit.Seed(fake, 50, "vn");
            fake.AddPlayer(TestKit.Uid(9999), 1, "us");
            LeaderboardConfig config = TestKit.Config(uid: () => TestKit.Uid(9999));
            LeaderboardSnapshot result = await Loader(config: config).LoadAsync("vn");
            Assert.IsTrue(result.SelfRankKnown);
            Assert.IsNull(result.SelfRank);
        });

        [UnityTest]
        public IEnumerator MoLaiTrongThoiGianCho_KhongGoiMang_ForceThiGoi() => UniTask.ToCoroutine(async () =>
        {
            TestKit.Seed(fake, 20);
            LeaderboardBoardLoader loader = Loader();
            await loader.LoadAsync("ww");
            int after = fake.Requests.Count;

            LeaderboardSnapshot again = await loader.LoadAsync("ww");
            Assert.AreEqual(after, fake.Requests.Count);
            Assert.AreEqual(LeaderboardLoadStage.Complete, again.Stage);

            await loader.LoadAsync("ww", force: true);
            Assert.Greater(fake.Requests.Count, after);

            int beforeInvalidate = fake.Requests.Count;
            loader.Invalidate("WW");
            await loader.LoadAsync("ww");
            Assert.Greater(fake.Requests.Count, beforeInvalidate);
        });

        [UnityTest]
        public IEnumerator MatMang_HienCacheKemLoi() => UniTask.ToCoroutine(async () =>
        {
            MemoryCache cache = new MemoryCache();
            cache.Store["ww"] = new LeaderboardCacheSnapshot
            {
                Board = "ww",
                SavedAtUnixSeconds = 1_700_000_000,
                BoardTotal = 1,
                Entries = new List<LeaderboardEntry> { new LeaderboardEntry { Rank = 1, Uid = TestKit.SelfUid, Score = 9 } },
                SelfRank = new LeaderboardRank { Rank = 1, Uid = TestKit.SelfUid, Total = 1 },
                SelfUid = TestKit.SelfUid,
            };
            fake.EnqueueResponse(0, null, networkError: true);
            List<LeaderboardSnapshot> updates = new List<LeaderboardSnapshot>();

            LeaderboardSnapshot result = await Loader(cache).LoadAsync("ww", updates.Add);

            Assert.AreEqual(LeaderboardLoadStage.Cached, updates[0].Stage);
            Assert.IsTrue(updates[0].IsFromCache);
            Assert.IsNotNull(updates[0].CachedAtLocalTime);
            Assert.AreEqual(LeaderboardLoadStage.Failed, result.Stage);
            Assert.AreEqual(LeaderboardErrorKind.Network, result.Error.Kind);
            Assert.AreEqual(1, result.Entries.Count, "vẫn giữ bảng cũ trên màn hình");
            Assert.AreEqual(1, result.SelfRank.Rank);
            Assert.AreEqual(0, cache.Saves, "lỗi thì không ghi đè cache");
        });

        [UnityTest]
        public IEnumerator CacheCuaTaiKhoanKhac_KhongHienHangCuaHo() => UniTask.ToCoroutine(async () =>
        {
            MemoryCache cache = new MemoryCache();
            cache.Store["ww"] = new LeaderboardCacheSnapshot
            {
                Board = "ww",
                Entries = new List<LeaderboardEntry> { new LeaderboardEntry { Rank = 1, Uid = "1999999999" } },
                SelfRank = new LeaderboardRank { Rank = 1, Uid = "1999999999" },
                SelfUid = "1999999999",
            };
            fake.EnqueueResponse(0, null, networkError: true);
            LeaderboardSnapshot result = await Loader(cache).LoadAsync("ww");
            Assert.IsNull(result.SelfRank);
            Assert.IsFalse(result.SelfRankKnown);
        });

        [UnityTest]
        public IEnumerator PhanSauTrangDauHong_BangCutVaKhongGhiCache() => UniTask.ToCoroutine(async () =>
        {
            TestKit.Seed(fake, 300);
            MemoryCache cache = new MemoryCache();
            LeaderboardSnapshot result = await Loader(cache, new FailNthTransport(fake, 2)).LoadAsync("ww");
            Assert.AreEqual(LeaderboardLoadStage.Complete, result.Stage);
            Assert.IsTrue(result.IsPartial);
            Assert.AreEqual(100, result.Entries.Count);
            Assert.AreEqual(LeaderboardErrorKind.Server, result.Error.Kind);
            Assert.AreEqual(0, cache.Saves);
        });

        [UnityTest]
        public IEnumerator GhiCacheDia_LoaderMoiHienNgayLanMoSau() => UniTask.ToCoroutine(async () =>
        {
            string dir = Path.Combine(Path.GetTempPath(), "abi-lb-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                TestKit.Seed(fake, 120);
                await Loader(new LeaderboardFileCache(dir)).LoadAsync("vn");

                fake.EnqueueResponse(503, "busy");
                List<LeaderboardSnapshot> updates = new List<LeaderboardSnapshot>();
                await Loader(new LeaderboardFileCache(dir)).LoadAsync("vn", updates.Add);

                Assert.IsTrue(updates[0].IsFromCache);
                Assert.AreEqual(120, updates[0].Entries.Count);
                Assert.AreEqual("P1", updates[0].Entries[0].GetInfoString("PlayerName"));
                Assert.AreEqual(1, updates[0].SelfRank.Rank);
            }
            finally
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, true);
            }
        });

        [UnityTest]
        public IEnumerator HuyKhiDangTai_NemOperationCanceled() => UniTask.ToCoroutine(async () =>
        {
            TestKit.Seed(fake, 10);
            fake.LatencySeconds = 2f;
            using (CancellationTokenSource cts = new CancellationTokenSource())
            {
                UniTask<LeaderboardSnapshot> task = Loader().LoadAsync("ww", cancellationToken: cts.Token);
                cts.CancelAfterSlim(TimeSpan.FromMilliseconds(100));
                bool canceled = false;
                try
                {
                    await task;
                }
                catch (OperationCanceledException)
                {
                    canceled = true;
                }
                Assert.IsTrue(canceled);
            }
        });

        [Test]
        public void BoardSai_NemArgumentException()
        {
            Assert.Throws<ArgumentException>(() => Loader().LoadAsync("world").GetAwaiter().GetResult());
        }
    }
}
