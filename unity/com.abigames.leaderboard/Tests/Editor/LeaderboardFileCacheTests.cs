using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace AbiGames.Leaderboard.Tests
{
    public class LeaderboardFileCacheTests
    {
        private string dir;

        [SetUp]
        public void SetUp() => dir = Path.Combine(Path.GetTempPath(), "abi-lb-cache-" + Guid.NewGuid().ToString("N"));

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }

        private static LeaderboardCacheSnapshot Sample(string board) => new LeaderboardCacheSnapshot
        {
            Board = board,
            SavedAtUnixSeconds = 1_760_000_000,
            BoardTotal = 2,
            Entries = new List<LeaderboardEntry>
            {
                new LeaderboardEntry { Rank = 1, Uid = "1000000001", Score = 9, SecondaryScore = 2, Info = JObject.FromObject(new { PlayerName = "An", Level = 9 }) },
                new LeaderboardEntry { Rank = 2, Uid = "1000000002", Score = 8 },
            },
            SelfRank = new LeaderboardRank { Rank = 1, Uid = "1000000001", Total = 77 },
            SelfUid = "1000000001",
        };

        [UnityTest]
        public IEnumerator GhiRoiDocLaiNguyenVen() => UniTask.ToCoroutine(async () =>
        {
            LeaderboardFileCache cache = new LeaderboardFileCache(dir);
            await cache.SaveAsync(Sample("vn"), CancellationToken.None);
            LeaderboardCacheSnapshot loaded = await cache.LoadAsync("VN", CancellationToken.None);

            Assert.AreEqual(2, loaded.Entries.Count);
            Assert.AreEqual("An", loaded.Entries[0].GetInfoString("PlayerName"));
            Assert.AreEqual(2, loaded.Entries[0].SecondaryScore);
            Assert.AreEqual(77, loaded.SelfRank.Total);
            Assert.AreEqual("1000000001", loaded.SelfUid);
            Assert.IsFalse(File.Exists(Path.Combine(dir, "vn.json.tmp")), "không để lại file tạm");
        });

        [UnityTest]
        public IEnumerator ChuaCo_HoacKhacPhienBan_TraNull() => UniTask.ToCoroutine(async () =>
        {
            LeaderboardFileCache cache = new LeaderboardFileCache(dir);
            Assert.IsNull(await cache.LoadAsync("ww", CancellationToken.None));

            LeaderboardCacheSnapshot old = Sample("ww");
            old.SchemaVersion = 999;
            await cache.SaveAsync(old, CancellationToken.None);
            Assert.IsNull(await cache.LoadAsync("ww", CancellationToken.None));
        });

        [UnityTest]
        public IEnumerator FileHong_TraNullKhongNem() => UniTask.ToCoroutine(async () =>
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "ww.json"), "{hỏng");
            // Ghi một cảnh báo (không làm test đỏ) rồi trả null.
            Assert.IsNull(await new LeaderboardFileCache(dir).LoadAsync("ww", CancellationToken.None));
        });

        [UnityTest]
        public IEnumerator XoaHet() => UniTask.ToCoroutine(async () =>
        {
            LeaderboardFileCache cache = new LeaderboardFileCache(dir);
            await cache.SaveAsync(Sample("ww"), CancellationToken.None);
            cache.ClearAll();
            Assert.IsNull(await cache.LoadAsync("ww", CancellationToken.None));
        });

        [Test]
        public void TaoTheoConfig_TachTheoStorageKey_VaTatDuoc()
        {
            LeaderboardConfig config = new LeaderboardConfig { CacheFolder = "abi-lb", StorageKey = "prod" };
            Assert.IsNotNull(LeaderboardFileCache.FromConfig(config));
            config.CacheFolder = null;
            Assert.IsNull(LeaderboardFileCache.FromConfig(config));
        }
    }
}
