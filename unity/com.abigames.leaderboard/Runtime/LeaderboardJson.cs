using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AbiGames.Leaderboard
{
    /// <summary>Đọc / ghi JSON của API. Newtonsoft (không phải JsonUtility) vì <c>info</c> là object tự do và vì chạy được trên thread pool.</summary>
    internal static class LeaderboardJson
    {
        internal static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            MissingMemberHandling = MissingMemberHandling.Ignore,
            // Giữ nguyên chuỗi ngày tháng như server gửi, không tự đổi múi giờ.
            DateParseHandling = DateParseHandling.None,
        };

        private static readonly JsonSerializer Serializer = JsonSerializer.Create(Settings);

        internal static string Serialize(object value) => JsonConvert.SerializeObject(value, Settings);

        /// <summary>
        /// Đọc body thành <typeparamref name="T"/>.
        ///  - Body rỗng hoặc <c>null</c> = "không có dữ liệu" (vd. <c>GET /rank</c> của uid không có
        ///    trên board: Nest gửi body RỖNG khi handler trả null) → thành công với giá trị null.
        ///  - Chấp nhận cả JSON thô lẫn vỏ <c>{ success, data }</c> — host có interceptor bọc response
        ///    vẫn dùng được.
        /// </summary>
        internal static bool TryParse<T>(string body, out T value, out string error) where T : class
        {
            value = null;
            error = null;
            string trimmed = body?.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed == "null")
                return true;

            try
            {
                JToken token = ParseToken(trimmed);
                if (token is JObject obj && obj["success"] != null && obj.ContainsKey("data"))
                    token = obj["data"];
                if (token == null || token.Type == JTokenType.Null)
                    return true;
                value = token.ToObject<T>(Serializer);
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }

        /// <summary>
        /// Câu báo lỗi trong body lỗi của Nest: <c>{ statusCode, message, error }</c>, trong đó
        /// <c>message</c> có thể là mảng (lỗi validate). Không đọc được thì trả <paramref name="fallback"/>.
        /// </summary>
        internal static string ReadErrorMessage(string body, string fallback)
        {
            if (string.IsNullOrWhiteSpace(body))
                return fallback;
            try
            {
                if (!(ParseToken(body) is JObject obj))
                    return fallback;
                JToken message = obj["message"];
                if (message is JArray array)
                    return string.Join("; ", array.Select(t => t.ToString()));
                if (message != null && message.Type != JTokenType.Null)
                    return message.ToString();
                return obj["error"]?.ToString() ?? fallback;
            }
            catch
            {
                return fallback;
            }
        }

        private static JToken ParseToken(string json)
        {
            using (JsonTextReader reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None })
            {
                return JToken.ReadFrom(reader);
            }
        }
    }
}
