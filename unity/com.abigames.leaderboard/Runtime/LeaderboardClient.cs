using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace AbiGames.Leaderboard
{
    /// <summary>
    /// Gọi ba API của server bảng xếp hạng. Không giữ trạng thái gì ngoài cấu hình — tải
    /// bảng có cache dùng <see cref="LeaderboardBoardLoader"/>, gửi điểm có gộp / thử lại
    /// dùng <see cref="LeaderboardScoreSubmitter"/>.
    ///
    /// Tạo và gọi từ main thread (UnityWebRequest chỉ chạy trên main thread).
    /// </summary>
    public sealed class LeaderboardClient
    {
        private const string AppVersionHeader = "X-App-Version";

        private readonly ILeaderboardTransport transport;
        /// <summary>Đọc một lần lúc tạo — <c>Application.version</c> chỉ gọi được trên main thread.</summary>
        private readonly string appVersion;

        public LeaderboardConfig Config { get; }

        public LeaderboardClient(LeaderboardConfig config, ILeaderboardTransport transport = null)
        {
            Config = config ?? throw new ArgumentNullException(nameof(config));
            this.transport = transport ?? new UnityWebRequestTransport();
            if (config.SendAppVersionHeader)
            {
                try { appVersion = Application.version; }
                catch { appVersion = null; }
            }
        }

        /// <summary>
        /// <c>POST /submit</c> — gửi điểm (Level) của người chơi đang đăng nhập. Server chỉ nhận
        /// nếu cao hơn điểm hiện tại. <paramref name="secondaryScore"/> bị server bỏ qua trừ
        /// khi bật <c>secondaryScore.acceptFromClient</c>.
        /// </summary>
        public UniTask<LeaderboardResult<LeaderboardSubmitResult>> SubmitScoreAsync(
            int score,
            int secondaryScore = 0,
            CancellationToken cancellationToken = default)
        {
            if (score < 0)
                return UniTask.FromResult(InvalidArgument<LeaderboardSubmitResult>("score phải ≥ 0"));

            Dictionary<string, object> body = new Dictionary<string, object> { ["score"] = score };
            if (secondaryScore > 0)
                body["secondaryScore"] = secondaryScore;
            return SendAsync<LeaderboardSubmitResult>("POST", "submit", body, true, false, cancellationToken);
        }

        /// <summary>
        /// <c>GET /top</c> — một trang của board. <paramref name="limit"/> và <paramref name="offset"/>
        /// được chặn trong giới hạn của server để tham số lệch không thành lỗi 400.
        /// </summary>
        public UniTask<LeaderboardResult<LeaderboardPage>> GetTopPageAsync(
            string board,
            int limit,
            int offset,
            CancellationToken cancellationToken = default)
        {
            string normalized = LeaderboardBoards.Normalize(board);
            if (!LeaderboardBoards.IsValid(normalized))
                return UniTask.FromResult(InvalidArgument<LeaderboardPage>($"board không hợp lệ: {board}"));

            int max = Math.Max(1, Config.MaxEntries);
            int safeLimit = Mathf.Clamp(limit, 1, max);
            int safeOffset = Mathf.Clamp(offset, 0, max - 1);
            string path = $"top?countryCode={Uri.EscapeDataString(normalized)}&limit={safeLimit}&offset={safeOffset}";
            // Trang đủ 1.000 dòng ~250 KB: parse trên thread pool cho khỏi giật frame.
            return SendAsync<LeaderboardPage>("GET", path, null, false, Config.ParseOnThreadPool, cancellationToken);
        }

        /// <summary>
        /// <c>GET /rank</c> — hạng của <paramref name="uid"/> trên board. Thành công với
        /// <c>Value == null</c> nghĩa là uid không có trên board đó.
        /// </summary>
        public UniTask<LeaderboardResult<LeaderboardRank>> GetRankAsync(
            string uid,
            string board,
            CancellationToken cancellationToken = default)
        {
            string normalized = LeaderboardBoards.Normalize(board);
            if (string.IsNullOrWhiteSpace(uid) || !LeaderboardBoards.IsValid(normalized))
                return UniTask.FromResult(InvalidArgument<LeaderboardRank>("cần uid và board hợp lệ"));

            string path = $"rank?uid={Uri.EscapeDataString(uid.Trim())}&countryCode={Uri.EscapeDataString(normalized)}";
            return SendAsync<LeaderboardRank>("GET", path, null, false, false, cancellationToken);
        }

        private async UniTask<LeaderboardResult<T>> SendAsync<T>(
            string method,
            string path,
            object body,
            bool requiresAuth,
            bool parseOnThreadPool,
            CancellationToken cancellationToken) where T : class
        {
            string json = body != null ? LeaderboardJson.Serialize(body) : null;
            bool refreshed = false;

            while (true)
            {
                string token = Config.AccessTokenProvider?.Invoke();
                if (requiresAuth && string.IsNullOrEmpty(token))
                {
                    if (!refreshed && await TryRefreshAsync())
                    {
                        refreshed = true;
                        continue;
                    }
                    return LeaderboardResult<T>.Fail(new LeaderboardError(LeaderboardErrorKind.Unauthorized, 0, "chưa đăng nhập"));
                }

                LeaderboardHttpRequest request = new LeaderboardHttpRequest
                {
                    Method = method,
                    Url = Config.ResolveUrl(path),
                    Body = json,
                    Headers = BuildHeaders(token, json != null),
                    TimeoutSeconds = Config.TimeoutSeconds,
                };

                LeaderboardHttpResponse response = await transport.SendAsync(request, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
#if UNITY_EDITOR
                if (Config.LogRequests)
                    Debug.Log($"[Leaderboard] {method} {request.Url} → {response.StatusCode} {response.Error}");
#endif

                if (response.StatusCode == 401 && requiresAuth && !refreshed && await TryRefreshAsync())
                {
                    refreshed = true;
                    continue;
                }

                LeaderboardError error = ClassifyError(response);
                if (error != null)
                    return LeaderboardResult<T>.Fail(error);

                return await ParseAsync<T>(response, parseOnThreadPool, cancellationToken);
            }
        }

        private async UniTask<bool> TryRefreshAsync()
        {
            if (Config.RefreshAccessToken == null)
                return false;
            try
            {
                return await Config.RefreshAccessToken();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Leaderboard] làm mới token lỗi: {e.Message}");
                return false;
            }
        }

        private async UniTask<LeaderboardResult<T>> ParseAsync<T>(
            LeaderboardHttpResponse response,
            bool parseOnThreadPool,
            CancellationToken cancellationToken) where T : class
        {
            T value;
            string parseError;
            bool ok;
#if UNITY_WEBGL && !UNITY_EDITOR
            parseOnThreadPool = false;
#endif
            if (parseOnThreadPool)
            {
                await UniTask.SwitchToThreadPool();
                try
                {
                    ok = LeaderboardJson.TryParse(response.Body, out value, out parseError);
                }
                finally
                {
                    await UniTask.SwitchToMainThread();
                }
                cancellationToken.ThrowIfCancellationRequested();
            }
            else
            {
                ok = LeaderboardJson.TryParse(response.Body, out value, out parseError);
            }

            return ok
                ? LeaderboardResult<T>.Ok(value)
                : LeaderboardResult<T>.Fail(new LeaderboardError(LeaderboardErrorKind.Parse, response.StatusCode, parseError));
        }

        private IReadOnlyDictionary<string, string> BuildHeaders(string token, bool hasBody)
        {
            Dictionary<string, string> headers = new Dictionary<string, string> { ["Accept"] = "application/json" };
            if (hasBody)
                headers["Content-Type"] = "application/json";
            if (!string.IsNullOrEmpty(appVersion))
                headers[AppVersionHeader] = appVersion;
            if (Config.ExtraHeaders != null)
            {
                foreach (KeyValuePair<string, string> pair in Config.ExtraHeaders)
                    headers[pair.Key] = pair.Value;
            }
            if (!string.IsNullOrEmpty(token))
                headers["Authorization"] = "Bearer " + token;
            return headers;
        }

        /// <summary>Null = thành công (2xx).</summary>
        internal static LeaderboardError ClassifyError(LeaderboardHttpResponse response)
        {
            if (response.IsTimeout)
                return new LeaderboardError(LeaderboardErrorKind.Timeout, 0, response.Error ?? "timeout");
            if (response.IsNetworkError || response.StatusCode == 0)
                return new LeaderboardError(LeaderboardErrorKind.Network, 0, response.Error ?? "không tới được server");

            long status = response.StatusCode;
            if (status >= 200 && status < 300)
                return null;

            string message = LeaderboardJson.ReadErrorMessage(response.Body, response.Error ?? $"HTTP {status}");
            LeaderboardErrorKind kind;
            switch (status)
            {
                case 400:
                    kind = LeaderboardErrorKind.BadRequest;
                    break;
                case 401:
                    kind = LeaderboardErrorKind.Unauthorized;
                    break;
                case 403:
                    kind = LeaderboardErrorKind.Forbidden;
                    break;
                case 404:
                    kind = LeaderboardErrorKind.NotFound;
                    break;
                // nginx trả 429 khi vượt rate limit và 503 khi vượt `limit_conn` của /rank.
                case 429:
                case 503:
                    kind = LeaderboardErrorKind.RateLimited;
                    break;
                default:
                    kind = status >= 500 ? LeaderboardErrorKind.Server : LeaderboardErrorKind.BadRequest;
                    break;
            }
            return new LeaderboardError(kind, status, message);
        }

        private static LeaderboardResult<T> InvalidArgument<T>(string message) =>
            LeaderboardResult<T>.Fail(new LeaderboardError(LeaderboardErrorKind.InvalidArgument, 0, message));
    }
}
