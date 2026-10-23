namespace AbiGames.Leaderboard
{
    /// <summary>
    /// Chỗ giữ client / loader / submitter dùng chung cho cả game, để view UI tự tìm thấy mà
    /// không cần DI. Game có DI riêng thì bỏ qua lớp này và truyền thẳng loader vào view
    /// (<c>LeaderboardBoardView.Bind</c>).
    ///
    /// Gọi <see cref="Initialize"/> một lần sau khi biết base URL (thường là lúc boot), trên main thread.
    /// </summary>
    public static class LeaderboardRuntime
    {
        public static LeaderboardClient Client { get; private set; }
        public static LeaderboardBoardLoader Loader { get; private set; }
        public static LeaderboardScoreSubmitter Submitter { get; private set; }
        public static ILeaderboardCache Cache { get; private set; }

        public static bool IsInitialized => Client != null;

        /// <param name="transport">Null = UnityWebRequest. Truyền <c>FakeLeaderboardTransport</c> để chạy không cần server.</param>
        /// <param name="cache">Null = cache file theo <see cref="LeaderboardConfig.CacheFolder"/>.</param>
        public static void Initialize(
            LeaderboardConfig config,
            ILeaderboardTransport transport = null,
            ILeaderboardCache cache = null,
            LeaderboardSubmitterOptions submitterOptions = null)
        {
            Shutdown();
            Client = new LeaderboardClient(config, transport);
            Cache = cache ?? LeaderboardFileCache.FromConfig(config);
            Loader = new LeaderboardBoardLoader(Client, Cache);
            Submitter = new LeaderboardScoreSubmitter(Client, submitterOptions);
        }

        /// <summary>Dừng gửi điểm nền và bỏ mọi tham chiếu (vd. khi đăng xuất / đổi môi trường).</summary>
        public static void Shutdown()
        {
            Submitter?.Dispose();
            Submitter = null;
            Loader = null;
            Cache = null;
            Client = null;
        }
    }
}
