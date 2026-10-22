/** Một dòng của bảng xếp hạng như client nhận được. */
export interface LeaderboardTopEntry {
  rank: number;
  uid: string;
  score: number;
  countryCode: string | null;
  /**
   * Điểm phụ dùng để phân định khi bằng điểm (vd. Crown). Chỉ có khi bật
   * `secondaryScore` VÀ giá trị khác 0 — vắng mặt nghĩa là 0.
   */
  secondaryScore?: number;
  /** Profile để vẽ dòng (tên, avatar…): chỉ gồm các key khai báo trong option `profile`. */
  info: Record<string, unknown>;
}

export interface LeaderboardTopResponse {
  countryCode: string;
  /** Số dòng TRẢ VỀ trong `entries`. */
  total: number;
  /** Tổng số dòng của board (≤ `topLimit`) — client dùng để biết còn bao nhiêu trang. */
  boardTotal: number;
  entries: LeaderboardTopEntry[];
}

export interface LeaderboardRankResult {
  rank: number;
  uid: string;
  score: number;
  secondaryScore: number;
  countryCode: string | null;
  board: string;
  /** Tổng số người trên board (đọc từ histogram nên có thể trễ tối đa một nhịp cron). */
  total: number;
}

export interface LeaderboardSubmitResult {
  playerId: string;
  uid: string;
  score: number;
  secondaryScore: number;
  countryCode: string | null;
  scoreUpdatedAt: Date;
}

/** Một lần ghi điểm xuống `leaderboard_entries` (đi qua hàng đợi hoặc ghi thẳng). */
export interface ScoreWrite {
  playerId: string;
  uid: string;
  score: number;
  countryCode: string | null;
  secondaryScore: number;
  season: number;
}

/** Dòng đọc từ DB, đủ cho dựng bảng và tính hạng. */
export interface EntryRow {
  id: string;
  playerId: string;
  uid: string;
  score: number;
  secondaryScore: number;
  season: number;
  countryCode: string | null;
  /** `rankedAt` dạng chuỗi giữ đủ micro giây — Date của JS chỉ tới mili giây. */
  rankedAt: string;
}
