/**
 * Mọi key Redis của package ở một chỗ. `v1` nằm trong key để lần đổi định dạng sau chỉ
 * cần tăng số — cache cũ tự hết hạn, không phải xoá tay.
 *
 * Không có key nào phải tìm bằng SCAN: Redis của game thường có hàng triệu key và quét
 * toàn keyspace từng đẩy p95 của Colony Flow lên 4–8 giây.
 */
export class LeaderboardKeys {
  constructor(private readonly prefix: string) {}

  private k(rest: string): string {
    return `${this.prefix}v1:${rest}`;
  }

  /** Board đã dựng (mảng JSON top N). */
  board(board: string): string {
    return this.k(`board:${board}`);
  }

  /** Body JSON đã serialize của một trang — request phổ biến chỉ tốn đúng một GET. */
  page(board: string, offset: number, limit: number): string {
    return this.k(`page:${board}:${offset}:${limit}`);
  }

  rank(board: string, uid: string): string {
    return this.k(`rank:${board}:${uid}`);
  }

  histogram(board: string): string {
    return this.k(`hist:${board}`);
  }

  /** Cache userId → player cho đường submit. */
  player(userId: string): string {
    return this.k(`player:${userId}`);
  }

  /** Điểm đã xếp hàng gần nhất của player — lần gửi không cao hơn thì khỏi xếp hàng. */
  lastQueued(playerId: string): string {
    return this.k(`last:${playerId}`);
  }

  pending(): string {
    return this.k('score-pending');
  }

  inflight(workerId: string): string {
    return this.k(`score-inflight:${workerId}`);
  }

  /** Tập id của mọi worker từng nhận lô — thay cho SCAN khi gom lô dở của worker đã chết. */
  inflightRegistry(): string {
    return this.k('score-inflight-ids');
  }

  inflightAlive(workerId: string): string {
    return this.k(`score-inflight-alive:${workerId}`);
  }

  lock(name: string): string {
    return this.k(`lock:${name}`);
  }

  seasonApplied(): string {
    return this.k('season-applied');
  }
}
