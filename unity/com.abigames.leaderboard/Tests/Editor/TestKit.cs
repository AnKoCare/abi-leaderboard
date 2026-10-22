using System;
using System.Collections.Generic;
using System.Threading;
using AbiGames.Leaderboard.Testing;
using Cysharp.Threading.Tasks;
using NUnit.Framework;

namespace AbiGames.Leaderboard.Tests
{
    internal static class TestKit
    {
        public const string SelfUid = "1000000001";
        public const string Token = "tok-self";

        public static LeaderboardConfig Config(Func<string> token = null, Func<string> uid = null, string storageKey = null)
        {
            return new LeaderboardConfig
            {
                BaseUrl = "https://fake.test/api/v1",
                AccessTokenProvider = token ?? (() => Token),
                SelfUidProvider = uid ?? (() => SelfUid),
                CacheFolder = null,
                ParseOnThreadPool = false,
                RefreshCooldownSeconds = 300f,
                StorageKey = storageKey ?? "test-" + Guid.NewGuid().ToString("N").Substring(0, 8),
            };
        }

        /// <summary>uid 10 chữ số, không bắt đầu bằng 0 (giống template).</summary>
        public static string Uid(int n) => (1_000_000_000L + n).ToString();

        /// <summary>Thêm <paramref name="count"/> người, điểm giảm dần: Uid(1) đứng đầu.</summary>
        public static void Seed(FakeLeaderboardTransport fake, int count, string country = "vn")
        {
            for (int i = 1; i <= count; i++)
                fake.AddPlayer(Uid(i), 100_000 - i, country, new { PlayerName = "P" + i, Level = 100_000 - i });
        }

        public static async UniTask WaitUntil(Func<bool> condition, float timeoutSeconds = 5f)
        {
            using (CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
            {
                try
                {
                    await UniTask.WaitUntil(condition, PlayerLoopTiming.Update, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    Assert.Fail($"quá {timeoutSeconds}s mà điều kiện chưa đúng");
                }
            }
        }
    }

    /// <summary>Cache trong bộ nhớ để test luồng "hiện cache trước" mà không đụng đĩa.</summary>
    internal sealed class MemoryCache : ILeaderboardCache
    {
        public readonly Dictionary<string, LeaderboardCacheSnapshot> Store = new Dictionary<string, LeaderboardCacheSnapshot>();
        public int Saves;

        public UniTask<LeaderboardCacheSnapshot> LoadAsync(string board, CancellationToken cancellationToken) =>
            UniTask.FromResult(Store.TryGetValue(board, out LeaderboardCacheSnapshot s) ? s : null);

        public UniTask SaveAsync(LeaderboardCacheSnapshot snapshot, CancellationToken cancellationToken)
        {
            Store[snapshot.Board] = snapshot;
            Saves += 1;
            return UniTask.CompletedTask;
        }

        public void ClearAll() => Store.Clear();
    }

    /// <summary>Làm hỏng đúng request thứ <c>failAt</c> (đếm từ 1), còn lại chuyển cho server giả.</summary>
    internal sealed class FailNthTransport : ILeaderboardTransport
    {
        private readonly ILeaderboardTransport inner;
        private readonly int failAt;
        private int count;

        public FailNthTransport(ILeaderboardTransport inner, int failAt)
        {
            this.inner = inner;
            this.failAt = failAt;
        }

        public UniTask<LeaderboardHttpResponse> SendAsync(LeaderboardHttpRequest request, CancellationToken cancellationToken)
        {
            count += 1;
            if (count == failAt)
                return UniTask.FromResult(new LeaderboardHttpResponse { StatusCode = 502, Body = "Bad Gateway" });
            return inner.SendAsync(request, cancellationToken);
        }
    }
}
