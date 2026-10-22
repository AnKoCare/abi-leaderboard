/**
 * Các điểm nối mà project chủ (host) cung cấp. Host dùng template game-backend thì
 * không phải viết gì: `players: 'template'` (mặc định) đã cài sẵn hai port đầu bằng
 * bảng `players`.
 */

/** Người chơi như bảng xếp hạng cần biết. */
export interface LeaderboardPlayer {
  /** Khoá của dòng BXH — mỗi player tối đa một dòng. */
  playerId: string;
  /** Mã công khai hiện trên BXH và dùng để tra hạng (template: 10 chữ số). */
  uid: string;
  /** ISO alpha-2 viết thường; null = chưa chọn quốc gia → chỉ có mặt ở board World. */
  countryCode: string | null;
}

export interface LeaderboardPlayerResolver {
  /** `userId` lấy từ request đã xác thực → player. Null = user chưa có player. */
  findByUserId(userId: string): Promise<LeaderboardPlayer | null>;
  /**
   * Tuỳ chọn. Có thì `GET /rank` tự sửa dòng BXH còn thiếu quốc gia bằng quốc gia
   * hiện tại của player.
   */
  findByUid?(uid: string): Promise<LeaderboardPlayer | null>;
}

export interface LeaderboardProfileProvider {
  /**
   * Nạp profile cho CẢ LÔ player (package gọi theo lô tối đa 500 id). Id nào không có
   * profile thì để trống trong Map — dòng đó vẫn lên bảng với giá trị `profile.defaults`.
   *
   * Hàm này nằm trên đường dựng bảng (1.000 dòng × số board, mỗi 20 phút): phải là MỘT
   * truy vấn theo lô (`IN (...)` hoặc Redis MGET), không được truy vấn từng id.
   */
  getProfiles(playerIds: string[]): Promise<Map<string, Record<string, unknown>>>;
}

export interface LeaderboardJobRunner {
  /**
   * Tiến trình này có được chạy cron và worker flush không. Chạy cluster thì chỉ đúng
   * MỘT tiến trình mỗi container được trả true.
   */
  isJobRunner(): boolean;
  /**
   * Chạy job nặng sao cho không chồng lên job nặng khác. Trả false nếu bỏ lượt.
   * Host có sẵn khoá job nặng chung (template: `runExclusiveHeavyJob`) thì nên cắm vào
   * đây để job của BXH xếp hàng chung với các job khác.
   */
  runExclusive(job: string, work: () => Promise<void>): Promise<boolean>;
}

export interface LeaderboardSeasonProvider {
  /**
   * Mùa hiện hành của điểm phụ (số nguyên ≥ 1, chỉ tăng). Sang mùa mới thì điểm phụ
   * của mọi người coi như về 0. Package gọi hàm này mỗi lần ghi điểm và mỗi phút một
   * lần — nên cache trong bộ nhớ.
   */
  currentSeason(): Promise<number>;
}
