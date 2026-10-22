using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AbiGames.Leaderboard
{
    /// <summary>Một dòng bảng xếp hạng.</summary>
    [Serializable]
    public class LeaderboardEntry
    {
        [JsonProperty("rank")] public int Rank { get; set; }
        [JsonProperty("uid")] public string Uid { get; set; }
        [JsonProperty("score")] public int Score { get; set; }

        /// <summary>Null = người chơi chưa chọn quốc gia.</summary>
        [JsonProperty("countryCode")] public string CountryCode { get; set; }

        /// <summary>Điểm phụ (phân định khi bằng điểm). Server bỏ trống khi bằng 0 hoặc khi tắt tính năng.</summary>
        [JsonProperty("secondaryScore")] public int SecondaryScore { get; set; }

        /// <summary>
        /// Profile để vẽ dòng: chỉ gồm các key server khai báo trong option <c>profile</c>
        /// (template: Level, PlayerName, AvatarIndex, FrameIndex, BadgeIndex). Có thể null.
        /// </summary>
        [JsonProperty("info")] public JObject Info { get; set; }

        /// <summary>Giá trị chuỗi trong <see cref="Info"/>; thiếu / null → <paramref name="fallback"/>.</summary>
        public string GetInfoString(string key, string fallback = null)
        {
            JToken token = Info?[key];
            if (token == null || token.Type == JTokenType.Null)
                return fallback;
            return token.Type == JTokenType.String ? (string)token : token.ToString(Formatting.None);
        }

        /// <summary>Giá trị số nguyên trong <see cref="Info"/>; thiếu / null / sai kiểu → <paramref name="fallback"/>.</summary>
        public int GetInfoInt(string key, int fallback = 0)
        {
            JToken token = Info?[key];
            if (token == null)
                return fallback;
            switch (token.Type)
            {
                case JTokenType.Integer:
                    return (int)token;
                case JTokenType.Float:
                    return (int)(double)token;
                case JTokenType.String:
                    return int.TryParse((string)token, out int parsed) ? parsed : fallback;
                default:
                    return fallback;
            }
        }

        /// <summary>Đọc <see cref="Info"/> thành class của game (vd. <c>PlayerProfileInfo</c>). Null nếu không có info.</summary>
        public T InfoAs<T>() where T : class
        {
            return Info?.ToObject<T>();
        }
    }

    /// <summary>Một trang của <c>GET /top</c>.</summary>
    [Serializable]
    public class LeaderboardPage
    {
        [JsonProperty("countryCode")] public string CountryCode { get; set; }

        /// <summary>Số dòng TRẢ VỀ trong <see cref="Entries"/>.</summary>
        [JsonProperty("total")] public int Total { get; set; }

        /// <summary>Tổng số dòng của board (≤ topLimit). 0 với server cũ chưa có field này.</summary>
        [JsonProperty("boardTotal")] public int BoardTotal { get; set; }

        [JsonProperty("entries")] public List<LeaderboardEntry> Entries { get; set; } = new List<LeaderboardEntry>();

        /// <summary>Tổng dòng của board; server cũ không gửi <see cref="BoardTotal"/> thì chỉ biết được trang này.</summary>
        [JsonIgnore] public int KnownBoardTotal => BoardTotal > 0 ? BoardTotal : Total;
    }

    /// <summary>Kết quả <c>GET /rank</c>.</summary>
    [Serializable]
    public class LeaderboardRank
    {
        [JsonProperty("rank")] public int Rank { get; set; }
        [JsonProperty("uid")] public string Uid { get; set; }
        [JsonProperty("score")] public int Score { get; set; }
        [JsonProperty("secondaryScore")] public int SecondaryScore { get; set; }
        [JsonProperty("countryCode")] public string CountryCode { get; set; }
        [JsonProperty("board")] public string Board { get; set; }

        /// <summary>
        /// Tổng số người trên board (có thể lớn hơn topLimit). Khi hạng được suy ra từ chính
        /// bảng top (người chơi nằm trong top) thì đây là số dòng của board đã tải.
        /// </summary>
        [JsonProperty("total")] public int Total { get; set; }
    }

    /// <summary>Kết quả <c>POST /submit</c>.</summary>
    [Serializable]
    public class LeaderboardSubmitResult
    {
        [JsonProperty("playerId")] public string PlayerId { get; set; }
        [JsonProperty("uid")] public string Uid { get; set; }
        [JsonProperty("score")] public int Score { get; set; }
        [JsonProperty("secondaryScore")] public int SecondaryScore { get; set; }
        [JsonProperty("countryCode")] public string CountryCode { get; set; }
        [JsonProperty("scoreUpdatedAt")] public string ScoreUpdatedAt { get; set; }
    }
}
