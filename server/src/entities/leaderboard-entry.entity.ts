import {
  Column,
  CreateDateColumn,
  Entity,
  Index,
  PrimaryGeneratedColumn,
  UpdateDateColumn,
} from 'typeorm';
import { ENTRIES_TABLE } from '../leaderboard.constants';

/**
 * Một dòng bảng xếp hạng cho mỗi player.
 *
 * Tên bảng và tên cột giữ đúng như template game-backend để cài đè lên dữ liệu sẵn có:
 * cột `leaguePoint` chính là điểm phụ, `leagueCycle` là mùa của điểm phụ.
 *
 * Package KHÔNG khai báo khoá ngoại sang bảng player của host — host xoá player thì phải
 * gọi `LeaderboardService.removePlayer`.
 *
 * Hai index phục vụ thứ tự xếp hạng có chiều sắp xếp riêng từng cột
 * (`score DESC, leaguePoint DESC, rankedAt ASC`) nên không khai báo được bằng decorator;
 * migration `LeaderboardSchema1790000000000` tạo chúng bằng SQL. Đừng bật `synchronize`.
 */
@Entity(ENTRIES_TABLE)
@Index(['playerId'], { unique: true })
@Index(['uid'])
export class LeaderboardEntry {
  @PrimaryGeneratedColumn('uuid')
  id: string;

  @Column({ type: 'varchar', length: 36 })
  playerId: string;

  @Column({ type: 'varchar', length: 10 })
  uid: string;

  /** Điểm chính (Level). Chỉ tăng. */
  @Column({ type: 'int' })
  score: number;

  /** Điểm phụ của mùa `season` — phân định khi bằng điểm. 0 khi tắt tính năng. */
  @Column({ name: 'leaguePoint', type: 'int', default: 0 })
  secondaryScore: number;

  @Column({ name: 'leagueCycle', type: 'int', default: 1 })
  season: number;

  /** Null = chưa chọn quốc gia → chỉ có mặt ở board World. */
  @Column({ type: 'varchar', length: 2, nullable: true })
  countryCode: string | null;

  /** Lúc score đạt giá trị hiện tại. */
  @Column({ type: 'datetime', precision: 6 })
  scoreUpdatedAt: Date;

  /** Lúc đạt đủ cặp (score, điểm phụ) hiện tại — ai đạt trước đứng trên. */
  @Column({ type: 'datetime', precision: 6 })
  rankedAt: Date;

  @CreateDateColumn({ type: 'datetime', precision: 6 })
  createdAt: Date;

  @UpdateDateColumn({ type: 'datetime', precision: 6 })
  updatedAt: Date;
}
