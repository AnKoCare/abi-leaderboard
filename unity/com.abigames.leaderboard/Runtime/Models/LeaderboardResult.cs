namespace AbiGames.Leaderboard
{
    public enum LeaderboardErrorKind
    {
        /// <summary>Không tới được server (mất mạng, DNS...).</summary>
        Network = 0,
        Timeout = 1,
        /// <summary>401 — chưa đăng nhập / token hết hạn.</summary>
        Unauthorized = 2,
        /// <summary>403 — uid bị cấm lên bảng.</summary>
        Forbidden = 3,
        /// <summary>404 — server không có player cho tài khoản này.</summary>
        NotFound = 4,
        /// <summary>400 — tham số sai.</summary>
        BadRequest = 5,
        /// <summary>429 / 503 do nginx chặn tải — nên thử lại sau.</summary>
        RateLimited = 6,
        /// <summary>5xx.</summary>
        Server = 7,
        /// <summary>Body không đọc được.</summary>
        Parse = 8,
        /// <summary>Tham số không hợp lệ, bị chặn ngay ở client.</summary>
        InvalidArgument = 9,
    }

    public sealed class LeaderboardError
    {
        public LeaderboardErrorKind Kind { get; }
        /// <summary>Mã HTTP; 0 khi không có response.</summary>
        public long StatusCode { get; }
        public string Message { get; }

        public LeaderboardError(LeaderboardErrorKind kind, long statusCode, string message)
        {
            Kind = kind;
            StatusCode = statusCode;
            Message = message;
        }

        /// <summary>Lỗi tạm thời — thử lại sau có thể được.</summary>
        public bool IsRetryable =>
            Kind == LeaderboardErrorKind.Network
            || Kind == LeaderboardErrorKind.Timeout
            || Kind == LeaderboardErrorKind.RateLimited
            || Kind == LeaderboardErrorKind.Server;

        public override string ToString() => $"{Kind} ({StatusCode}): {Message}";
    }

    /// <summary>
    /// Kết quả một lần gọi API. Lỗi mạng / lỗi server là chuyện thường ngày trên mobile nên
    /// trả về như dữ liệu thay vì ném exception; riêng huỷ (CancellationToken) vẫn ném
    /// <see cref="System.OperationCanceledException"/> như mọi API UniTask.
    /// </summary>
    public readonly struct LeaderboardResult<T>
    {
        public bool IsSuccess { get; }

        /// <summary>Có thể là null khi thành công — vd. <c>GET /rank</c> của uid không có trên board.</summary>
        public T Value { get; }

        public LeaderboardError Error { get; }

        private LeaderboardResult(bool isSuccess, T value, LeaderboardError error)
        {
            IsSuccess = isSuccess;
            Value = value;
            Error = error;
        }

        public static LeaderboardResult<T> Ok(T value) => new LeaderboardResult<T>(true, value, null);
        public static LeaderboardResult<T> Fail(LeaderboardError error) => new LeaderboardResult<T>(false, default, error);
    }
}
