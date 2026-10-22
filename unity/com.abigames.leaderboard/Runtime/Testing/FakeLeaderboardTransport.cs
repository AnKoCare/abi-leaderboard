using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AbiGames.Leaderboard.Testing
{
    /// <summary>
    /// Server bảng xếp hạng giả lập trong bộ nhớ — cho test, bản demo và dựng UI khi chưa có
    /// backend. Trả đúng định dạng của <c>@abigames/leaderboard-server</c> (kể cả body RỖNG của
    /// <c>/rank</c> khi không có trên board) và xếp hạng đúng thứ tự của server: điểm giảm dần →
    /// điểm phụ giảm dần → ai đạt trước đứng trên.
    ///
    /// Không giả lập cache của server: điểm vừa gửi lên bảng ngay.
    /// </summary>
    public sealed class FakeLeaderboardTransport : ILeaderboardTransport
    {
        public sealed class Player
        {
            public string Uid;
            public string CountryCode;
            public JObject Info;
            public int Score;
            public int SecondaryScore;
            public bool Blocked;
            internal long AchievedSeq;
        }

        private readonly Dictionary<string, Player> players = new Dictionary<string, Player>();
        private readonly Dictionary<string, string> uidByToken = new Dictionary<string, string>();
        private readonly Queue<LeaderboardHttpResponse> scripted = new Queue<LeaderboardHttpResponse>();
        private long seq;

        /// <summary>Số dòng tối đa mỗi board (option <c>topLimit</c> của server).</summary>
        public int MaxEntries = 1000;
        /// <summary>Bọc response trong <c>{ success, data }</c> như host có interceptor.</summary>
        public bool WrapInEnvelope;
        /// <summary>Server có nhận điểm phụ từ client không (<c>secondaryScore.acceptFromClient</c>).</summary>
        public bool AcceptSecondaryFromClient;
        /// <summary>Độ trễ giả lập mỗi request.</summary>
        public float LatencySeconds;

        /// <summary>Mọi request đã nhận, theo thứ tự.</summary>
        public List<LeaderboardHttpRequest> Requests { get; } = new List<LeaderboardHttpRequest>();

        public IReadOnlyDictionary<string, Player> Players => players;

        public Player AddPlayer(string uid, int score, string countryCode = null, object info = null, int secondaryScore = 0)
        {
            Player player = new Player
            {
                Uid = uid,
                Score = score,
                CountryCode = LeaderboardBoards.Normalize(countryCode),
                Info = info == null ? null : JObject.FromObject(info),
                SecondaryScore = secondaryScore,
                AchievedSeq = ++seq,
            };
            players[uid] = player;
            return player;
        }

        /// <summary>Gắn access token với uid — request <c>/submit</c> mang token này được coi là của uid đó.</summary>
        public void SetToken(string token, string uid) => uidByToken[token] = uid;

        /// <summary>Ép request kế tiếp nhận đúng response này (để thử lỗi). Gọi nhiều lần = xếp hàng.</summary>
        public void EnqueueResponse(long statusCode, string body = null, bool networkError = false, bool timeout = false)
        {
            scripted.Enqueue(new LeaderboardHttpResponse
            {
                StatusCode = networkError || timeout ? 0 : statusCode,
                Body = body,
                IsNetworkError = networkError || timeout,
                IsTimeout = timeout,
                Error = networkError || timeout ? (timeout ? "Request timeout" : "Cannot resolve destination host") : null,
            });
        }

        public void EnqueueFailures(int count, long statusCode = 503)
        {
            for (int i = 0; i < count; i++)
                EnqueueResponse(statusCode, "{\"statusCode\":" + statusCode + ",\"message\":\"giả lập lỗi\"}");
        }

        /// <summary>Số request tới đường dẫn kết thúc bằng <paramref name="path"/> (vd. "/top").</summary>
        public int CountRequests(string path) => Requests.Count(r => new Uri(r.Url).AbsolutePath.EndsWith(path, StringComparison.Ordinal));

        public async UniTask<LeaderboardHttpResponse> SendAsync(LeaderboardHttpRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (LatencySeconds > 0f)
                await UniTask.Delay(TimeSpan.FromSeconds(LatencySeconds), DelayType.Realtime, PlayerLoopTiming.Update, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (scripted.Count > 0)
                return scripted.Dequeue();

            Uri uri = new Uri(request.Url);
            Dictionary<string, string> query = ParseQuery(uri.Query);
            string path = uri.AbsolutePath;
            if (request.Method == "GET" && path.EndsWith("/top", StringComparison.Ordinal))
                return Top(query);
            if (request.Method == "GET" && path.EndsWith("/rank", StringComparison.Ordinal))
                return Rank(query);
            if (request.Method == "POST" && path.EndsWith("/submit", StringComparison.Ordinal))
                return Submit(request);
            return Json(404, new { statusCode = 404, message = "Not Found" }, false);
        }

        private IEnumerable<Player> Ordered(string board)
        {
            IEnumerable<Player> source = players.Values.Where(p => !p.Blocked);
            if (board != LeaderboardBoards.World)
                source = source.Where(p => p.CountryCode == board);
            return source
                .OrderByDescending(p => p.Score)
                .ThenByDescending(p => p.SecondaryScore)
                .ThenBy(p => p.AchievedSeq)
                .ThenBy(p => p.Uid, StringComparer.Ordinal);
        }

        private LeaderboardHttpResponse Top(Dictionary<string, string> query)
        {
            string board = LeaderboardBoards.Normalize(Get(query, "countryCode"));
            if (!LeaderboardBoards.IsValid(board))
                return Json(400, new { statusCode = 400, message = "countryCode must be 'ww' or a valid ISO 3166-1 alpha-2 code" }, false);
            int limit = int.TryParse(Get(query, "limit"), out int l) ? l : 100;
            int offset = int.TryParse(Get(query, "offset"), out int o) ? o : 0;

            List<Player> boardPlayers = Ordered(board).Take(MaxEntries).ToList();
            JArray entries = new JArray();
            for (int i = offset; i < Math.Min(boardPlayers.Count, offset + limit); i++)
            {
                Player p = boardPlayers[i];
                JObject entry = new JObject
                {
                    ["rank"] = i + 1,
                    ["uid"] = p.Uid,
                    ["score"] = p.Score,
                    ["countryCode"] = p.CountryCode == null ? JValue.CreateNull() : (JToken)p.CountryCode,
                    ["info"] = p.Info ?? new JObject(),
                };
                if (p.SecondaryScore > 0)
                    entry["secondaryScore"] = p.SecondaryScore;
                entries.Add(entry);
            }
            JObject page = new JObject
            {
                ["countryCode"] = board,
                ["total"] = entries.Count,
                ["boardTotal"] = boardPlayers.Count,
                ["entries"] = entries,
            };
            return Json(200, page, true);
        }

        private LeaderboardHttpResponse Rank(Dictionary<string, string> query)
        {
            string uid = Get(query, "uid");
            string board = LeaderboardBoards.Normalize(Get(query, "countryCode"));
            if (string.IsNullOrEmpty(uid) || !LeaderboardBoards.IsValid(board))
                return Json(400, new { statusCode = 400, message = "uid không đúng định dạng" }, false);

            List<Player> ordered = Ordered(board).ToList();
            int index = ordered.FindIndex(p => p.Uid == uid);
            // Như Nest: handler trả null → body rỗng.
            if (index < 0)
                return new LeaderboardHttpResponse { StatusCode = 200, Body = WrapInEnvelope ? "{\"success\":true,\"data\":null}" : string.Empty };

            Player player = ordered[index];
            return Json(200, new
            {
                rank = index + 1,
                uid = player.Uid,
                score = player.Score,
                secondaryScore = player.SecondaryScore,
                countryCode = player.CountryCode,
                board,
                total = ordered.Count,
            }, true);
        }

        private LeaderboardHttpResponse Submit(LeaderboardHttpRequest request)
        {
            string token = null;
            if (request.Headers != null && request.Headers.TryGetValue("Authorization", out string auth) && auth.StartsWith("Bearer ", StringComparison.Ordinal))
                token = auth.Substring("Bearer ".Length);
            if (token == null || !uidByToken.TryGetValue(token, out string uid))
                return Json(401, new { statusCode = 401, message = "Unauthorized" }, false);
            if (!players.TryGetValue(uid, out Player player))
                return Json(404, new { statusCode = 404, message = "Player not found for current user" }, false);
            if (player.Blocked)
                return Json(403, new { statusCode = 403, message = "Leaderboard submit is blocked for this player" }, false);

            JObject body = JObject.Parse(request.Body ?? "{}");
            int score = body.Value<int?>("score") ?? -1;
            if (score < 0)
                return Json(400, new { statusCode = 400, message = new[] { "score must not be less than 0" } }, false);
            int secondary = AcceptSecondaryFromClient ? body.Value<int?>("secondaryScore") ?? 0 : 0;

            bool improved = false;
            if (score > player.Score)
            {
                player.Score = score;
                improved = true;
            }
            if (secondary > player.SecondaryScore)
            {
                player.SecondaryScore = secondary;
                improved = true;
            }
            if (improved)
                player.AchievedSeq = ++seq;

            return Json(201, new
            {
                playerId = "p-" + player.Uid,
                uid = player.Uid,
                score,
                secondaryScore = secondary,
                countryCode = player.CountryCode,
                scoreUpdatedAt = DateTime.UtcNow.ToString("o"),
            }, true);
        }

        private LeaderboardHttpResponse Json(long status, object payload, bool success)
        {
            JToken token = payload as JToken ?? JToken.FromObject(payload);
            if (WrapInEnvelope && success)
                token = new JObject { ["success"] = true, ["data"] = token };
            return new LeaderboardHttpResponse { StatusCode = status, Body = token.ToString(Formatting.None) };
        }

        private static string Get(Dictionary<string, string> query, string key) =>
            query.TryGetValue(key, out string value) ? value : null;

        private static Dictionary<string, string> ParseQuery(string query)
        {
            Dictionary<string, string> result = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(query))
                return result;
            foreach (string part in query.TrimStart('?').Split('&'))
            {
                if (part.Length == 0)
                    continue;
                int eq = part.IndexOf('=');
                string key = Uri.UnescapeDataString(eq < 0 ? part : part.Substring(0, eq));
                string value = eq < 0 ? string.Empty : Uri.UnescapeDataString(part.Substring(eq + 1));
                result[key] = value;
            }
            return result;
        }
    }
}
