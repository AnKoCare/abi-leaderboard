namespace AbiGames.Leaderboard
{
    /// <summary>Mã board: <see cref="World"/> cho tab World, mã ISO 3166-1 alpha-2 viết thường cho từng quốc gia.</summary>
    public static class LeaderboardBoards
    {
        /// <summary>Board toàn cầu. Không phải mã ISO nên không trùng quốc gia nào.</summary>
        public const string World = "ww";

        /// <summary>Chuẩn hoá về dạng server nhận (cắt khoảng trắng, viết thường). Rỗng → null.</summary>
        public static string Normalize(string countryCode)
        {
            if (string.IsNullOrWhiteSpace(countryCode))
                return null;
            return countryCode.Trim().ToLowerInvariant();
        }

        /// <summary>Đúng dạng hai chữ cái (chưa chắc server có board đó — server mới là nơi kiểm danh sách).</summary>
        public static bool IsValid(string board)
        {
            return board != null
                   && board.Length == 2
                   && board[0] >= 'a' && board[0] <= 'z'
                   && board[1] >= 'a' && board[1] <= 'z';
        }
    }
}
