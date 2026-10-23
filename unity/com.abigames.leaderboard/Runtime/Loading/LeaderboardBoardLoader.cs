using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace AbiGames.Leaderboard
{
    public enum LeaderboardLoadStage
    {
        /// <summary>Đang hiện dữ liệu cũ (cache đĩa hoặc lần tải trước) trong lúc chờ mạng.</summary>
        Cached = 0,
        /// <summary>Đã có trang đầu; phần còn lại đang tải nền.</summary>
        FirstPage = 1,
        /// <summary>Xong. <see cref="LeaderboardSnapshot.IsPartial"/> = true nếu phần sau trang đầu tải hỏng.</summary>
        Complete = 2,
        /// <summary>Không tải được trang đầu — <see cref="LeaderboardSnapshot.Entries"/> là dữ liệu cũ (có thể rỗng).</summary>
        Failed = 3,
    }

    /// <summary>Trạng thái một board tại một thời điểm — UI vẽ thẳng từ đây.</summary>
    public sealed class LeaderboardSnapshot
    {
        public string Board { get; internal set; }
        public LeaderboardLoadStage Stage { get; internal set; }
        public IReadOnlyList<LeaderboardEntry> Entries { get; internal set; } = Array.Empty<LeaderboardEntry>();
        /// <summary>Tổng dòng của board (≤ topLimit).</summary>
        public int BoardTotal { get; internal set; }
        /// <summary>Hạng của người chơi; null = không có trên board, hoặc chưa biết (xem <see cref="SelfRankKnown"/>).</summary>
        public LeaderboardRank SelfRank { get; internal set; }
        /// <summary>False = không xác định được (chưa có uid, hoặc tra hạng lỗi).</summary>
        public bool SelfRankKnown { get; internal set; }
        /// <summary>Dữ liệu đang hiện lấy từ cache đĩa — nên ghi "dữ liệu lúc ...".</summary>
        public bool IsFromCache { get; internal set; }
        public DateTime? CachedAtLocalTime { get; internal set; }
        /// <summary>Tải phần sau trang đầu hỏng: bảng chỉ có trang đầu.</summary>
        public bool IsPartial { get; internal set; }
        /// <summary>Lỗi gần nhất (kể cả khi vẫn hiện được dữ liệu cũ).</summary>
        public LeaderboardError Error { get; internal set; }

        internal LeaderboardSnapshot With(Action<LeaderboardSnapshot> change)
        {
            LeaderboardSnapshot copy = (LeaderboardSnapshot)MemberwiseClone();
            change(copy);
            return copy;
        }
    }

    /// <summary>
    /// Tải một board theo luồng đã dùng ở Colony Flow:
    ///  1. hiện ngay dữ liệu cũ (lần tải trước trong phiên, hoặc cache đĩa) để không phải chờ mạng;
    ///  2. tải trang đầu (100 dòng) → thay dữ liệu đang hiện;
    ///  3. tải nền phần còn lại tới 1.000 dòng → nối vào;
    ///  4. tìm "hạng của tôi": có trong bảng thì lấy luôn, không thì gọi <c>GET /rank</c>;
    ///  5. ghi cache đĩa cho lần sau.
    ///
    /// Mỗi bước gọi <c>onUpdate</c> với một snapshot MỚI (không sửa snapshot cũ) để UI vẽ lại.
    /// Mở lại cùng board trong <see cref="LeaderboardConfig.RefreshCooldownSeconds"/> thì dùng
    /// luôn dữ liệu đã có, không gọi mạng.
    ///
    /// Huỷ bằng <c>cancellationToken</c> khi đổi tab / đóng bảng — hàm ném
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    public sealed class LeaderboardBoardLoader
    {
        private readonly LeaderboardClient client;
        private readonly ILeaderboardCache cache;
        private readonly Dictionary<string, LeaderboardSnapshot> latest = new Dictionary<string, LeaderboardSnapshot>();
        private readonly Dictionary<string, float> refreshAllowedAt = new Dictionary<string, float>();

        /// <param name="cache">Null = không cache đĩa. Thường dùng <see cref="LeaderboardFileCache.FromConfig"/>.</param>
        public LeaderboardBoardLoader(LeaderboardClient client, ILeaderboardCache cache = null)
        {
            this.client = client ?? throw new ArgumentNullException(nameof(client));
            this.cache = cache;
        }

        public LeaderboardConfig Config => client.Config;

        /// <summary>Snapshot gần nhất của board trong phiên này (null nếu chưa tải lần nào).</summary>
        public LeaderboardSnapshot GetLatest(string board)
        {
            string key = LeaderboardBoards.Normalize(board);
            return key != null && latest.TryGetValue(key, out LeaderboardSnapshot snapshot) ? snapshot : null;
        }

        /// <summary>Bỏ thời gian chờ để lần <see cref="LoadAsync"/> sau tải lại từ mạng. Null = mọi board.</summary>
        public void Invalidate(string board = null)
        {
            if (board == null)
                refreshAllowedAt.Clear();
            else
                refreshAllowedAt.Remove(LeaderboardBoards.Normalize(board) ?? string.Empty);
        }

        /// <param name="force">Bỏ qua thời gian chờ, luôn gọi mạng.</param>
        public async UniTask<LeaderboardSnapshot> LoadAsync(
            string board,
            Action<LeaderboardSnapshot> onUpdate = null,
            bool force = false,
            CancellationToken cancellationToken = default)
        {
            string key = LeaderboardBoards.Normalize(board);
            if (!LeaderboardBoards.IsValid(key))
                throw new ArgumentException($"board không hợp lệ: {board}", nameof(board));

            LeaderboardConfig config = client.Config;
            string selfUid = config.SelfUidProvider?.Invoke();
            latest.TryGetValue(key, out LeaderboardSnapshot current);

            // Mới tải xong gần đây: server chưa dựng lại bảng, gọi lại chỉ tốn request.
            if (!force && current != null && current.Stage == LeaderboardLoadStage.Complete
                && refreshAllowedAt.TryGetValue(key, out float allowedAt) && Now() < allowedAt)
            {
                onUpdate?.Invoke(current);
                return current;
            }

            // 1. Dữ liệu cũ trước.
            if (current == null && cache != null)
            {
                LeaderboardCacheSnapshot cached = await cache.LoadAsync(key, cancellationToken);
                if (cached != null && cached.Entries.Count > 0)
                {
                    bool sameUser = !string.IsNullOrEmpty(selfUid) && cached.SelfUid == selfUid;
                    current = new LeaderboardSnapshot
                    {
                        Board = key,
                        Stage = LeaderboardLoadStage.Cached,
                        Entries = cached.Entries,
                        BoardTotal = cached.BoardTotal,
                        SelfRank = sameUser ? cached.SelfRank : null,
                        SelfRankKnown = sameUser,
                        IsFromCache = true,
                        CachedAtLocalTime = cached.SavedAtLocalTime,
                    };
                    Publish(key, current, onUpdate);
                }
            }
            else if (current != null)
            {
                current = current.With(s => s.Stage = LeaderboardLoadStage.Cached);
                Publish(key, current, onUpdate);
            }

            // 2. Trang đầu.
            int max = Math.Max(1, config.MaxEntries);
            LeaderboardResult<LeaderboardPage> first =
                await client.GetTopPageAsync(key, Math.Min(Math.Max(1, config.FirstPageSize), max), 0, cancellationToken);
            if (!first.IsSuccess || first.Value == null)
            {
                LeaderboardError error = first.Error
                    ?? new LeaderboardError(LeaderboardErrorKind.Parse, 0, "server không trả dữ liệu bảng");
                LeaderboardSnapshot failed = current != null
                    ? current.With(s => { s.Stage = LeaderboardLoadStage.Failed; s.Error = error; })
                    : new LeaderboardSnapshot { Board = key, Stage = LeaderboardLoadStage.Failed, Error = error };
                Publish(key, failed, onUpdate);
                return failed;
            }

            LeaderboardPage page = first.Value;
            List<LeaderboardEntry> entries = new List<LeaderboardEntry>(Math.Min(page.KnownBoardTotal, max));
            AddEntries(entries, page.Entries);
            int boardTotal = page.KnownBoardTotal;

            LeaderboardSnapshot snapshot = new LeaderboardSnapshot
            {
                Board = key,
                Stage = LeaderboardLoadStage.FirstPage,
                Entries = entries.ToArray(),
                BoardTotal = boardTotal,
                SelfRank = current?.SelfRank,
                SelfRankKnown = current?.SelfRankKnown ?? false,
            };
            Publish(key, snapshot, onUpdate);

            // 3. Phần còn lại.
            bool partial = false;
            LeaderboardError restError = null;
            int remaining = Math.Min(boardTotal, max) - entries.Count;
            if (remaining > 0 && entries.Count > 0)
            {
                LeaderboardResult<LeaderboardPage> rest =
                    await client.GetTopPageAsync(key, remaining, entries.Count, cancellationToken);
                if (rest.IsSuccess && rest.Value != null)
                {
                    AddEntries(entries, rest.Value.Entries);
                }
                else
                {
                    partial = true;
                    restError = rest.Error;
                }
            }

            // 4. Hạng của tôi.
            (LeaderboardRank selfRank, bool known, LeaderboardError rankError) =
                await ResolveSelfRankAsync(key, selfUid, entries, boardTotal, current, cancellationToken);

            LeaderboardSnapshot complete = new LeaderboardSnapshot
            {
                Board = key,
                Stage = LeaderboardLoadStage.Complete,
                Entries = entries.ToArray(),
                BoardTotal = boardTotal,
                SelfRank = selfRank,
                SelfRankKnown = known,
                IsPartial = partial,
                Error = restError ?? rankError,
            };
            Publish(key, complete, onUpdate);
            refreshAllowedAt[key] = Now() + Math.Max(0f, config.RefreshCooldownSeconds);

            // 5. Ghi cache — chỉ khi đã có đủ bảng, để lần sau không mở ra một bảng cụt.
            if (cache != null && !partial)
            {
                await cache.SaveAsync(new LeaderboardCacheSnapshot
                {
                    Board = key,
                    SavedAtUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    BoardTotal = boardTotal,
                    Entries = entries,
                    SelfRank = selfRank,
                    SelfUid = known ? selfUid : null,
                }, cancellationToken);
            }
            return complete;
        }

        private async UniTask<(LeaderboardRank rank, bool known, LeaderboardError error)> ResolveSelfRankAsync(
            string board,
            string selfUid,
            List<LeaderboardEntry> entries,
            int boardTotal,
            LeaderboardSnapshot previous,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(selfUid))
                return (null, false, null);

            // Có trong bảng thì khỏi gọi mạng.
            for (int i = 0; i < entries.Count; i++)
            {
                LeaderboardEntry entry = entries[i];
                if (entry.Uid == selfUid)
                {
                    return (new LeaderboardRank
                    {
                        Rank = entry.Rank,
                        Uid = entry.Uid,
                        Score = entry.Score,
                        SecondaryScore = entry.SecondaryScore,
                        CountryCode = entry.CountryCode,
                        Board = board,
                        Total = boardTotal,
                    }, true, null);
                }
            }

            // Board rỗng thì chắc chắn mình không có trên đó.
            if (entries.Count == 0)
                return (null, true, null);

            LeaderboardResult<LeaderboardRank> result = await client.GetRankAsync(selfUid, board, cancellationToken);
            if (result.IsSuccess)
                return (result.Value, true, null);

            // Tra hạng hỏng: giữ hạng cũ (nếu có) thay vì xoá trắng ô "hạng của tôi".
            return (previous?.SelfRank, previous?.SelfRankKnown ?? false, result.Error);
        }

        private void Publish(string key, LeaderboardSnapshot snapshot, Action<LeaderboardSnapshot> onUpdate)
        {
            latest[key] = snapshot;
            onUpdate?.Invoke(snapshot);
        }

        private static void AddEntries(List<LeaderboardEntry> target, List<LeaderboardEntry> source)
        {
            if (source == null)
                return;
            for (int i = 0; i < source.Count; i++)
            {
                if (source[i] != null)
                    target.Add(source[i]);
            }
        }

        private static float Now() => Time.realtimeSinceStartup;
    }
}
