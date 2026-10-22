using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;

namespace AbiGames.Leaderboard
{
    /// <summary>
    /// Cấu hình client bảng xếp hạng. Mọi thứ dính tới game (token, uid của người chơi)
    /// đi vào qua delegate để package không phụ thuộc SDK đăng nhập nào.
    /// </summary>
    public sealed class LeaderboardConfig
    {
        /// <summary>
        /// URL gốc của API, GỒM CẢ global prefix của server, không có dấu "/" cuối.
        /// Ví dụ <c>https://api.game.com/api/v1</c>.
        /// </summary>
        public string BaseUrl;

        /// <summary>Prefix route của module trên server (option <c>routePrefix</c>). Mặc định <c>leaderboard</c>.</summary>
        public string RoutePrefix = "leaderboard";

        /// <summary>Access token Bearer hiện tại; null/rỗng = chưa đăng nhập. Chỉ cần cho gửi điểm.</summary>
        public Func<string> AccessTokenProvider;

        /// <summary>
        /// Gọi khi server trả 401 lúc gửi điểm: làm mới token rồi trả true để client thử lại
        /// đúng một lần. Null = không thử lại.
        /// </summary>
        public Func<UniTask<bool>> RefreshAccessToken;

        /// <summary>uid công khai của người chơi đang đăng nhập — để tìm "hạng của tôi" và tách dữ liệu theo tài khoản.</summary>
        public Func<string> SelfUidProvider;

        /// <summary>Header gửi kèm mọi request (vd. header riêng của game). Không cần thêm Authorization ở đây.</summary>
        public IReadOnlyDictionary<string, string> ExtraHeaders;

        /// <summary>
        /// Gửi <c>X-App-Version: Application.version</c> như SDK BackendClient — server template
        /// dùng nó để chặn bản game cũ. Mặc định bật.
        /// </summary>
        public bool SendAppVersionHeader = true;

        /// <summary>Thời gian chờ tối đa một request (giây).</summary>
        public int TimeoutSeconds = 15;

        /// <summary>Số dòng của trang đầu — nhỏ để mở bảng thấy dữ liệu ngay, phần còn lại tải nền.</summary>
        public int FirstPageSize = 100;

        /// <summary>Số dòng tối đa một board (khớp option <c>topLimit</c> của server).</summary>
        public int MaxEntries = 1000;

        /// <summary>
        /// Trong khoảng này, mở lại cùng một board dùng luôn dữ liệu đã tải, không gọi mạng.
        /// Server chỉ dựng lại bảng ~20 phút một lần nên tải dồn dập chẳng thấy gì mới.
        /// </summary>
        public float RefreshCooldownSeconds = 300f;

        /// <summary>Thư mục cache trong <c>Application.persistentDataPath</c>. Null/rỗng = không cache đĩa.</summary>
        public string CacheFolder = "abi-leaderboard";

        /// <summary>
        /// Tách cache và điểm chờ gửi giữa các môi trường (dev/prod) hoặc các game dùng chung
        /// thiết bị test. Chỉ dùng chữ, số, '-' và '_'.
        /// </summary>
        public string StorageKey = "default";

        /// <summary>
        /// Parse JSON trang lớn (1.000 dòng ~250 KB) trên thread pool để không giật frame.
        /// Tự tắt trên WebGL (không có thread).
        /// </summary>
        public bool ParseOnThreadPool = true;

        /// <summary>In URL + mã trạng thái mỗi request ra Console (chỉ trong Editor).</summary>
        public bool LogRequests;

        internal string ResolveUrl(string path)
        {
            string root = (BaseUrl ?? string.Empty).TrimEnd('/');
            string prefix = (RoutePrefix ?? string.Empty).Trim('/');
            return string.IsNullOrEmpty(prefix) ? $"{root}/{path}" : $"{root}/{prefix}/{path}";
        }
    }
}
