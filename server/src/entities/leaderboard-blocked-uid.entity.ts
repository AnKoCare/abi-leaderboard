import {
  Column,
  CreateDateColumn,
  Entity,
  Index,
  PrimaryGeneratedColumn,
  UpdateDateColumn,
} from 'typeorm';
import { BLOCKED_UIDS_TABLE } from '../leaderboard.constants';

/** uid bị cấm lên bảng (gian lận). Giữ đúng schema của template game-backend. */
@Entity(BLOCKED_UIDS_TABLE)
@Index(['uid'], { unique: true })
export class LeaderboardBlockedUid {
  @PrimaryGeneratedColumn('uuid')
  id: string;

  @Column({ type: 'varchar', length: 10 })
  uid: string;

  @Column({ type: 'text', nullable: true })
  reason: string | null;

  @CreateDateColumn({ type: 'datetime', precision: 6 })
  createdAt: Date;

  @UpdateDateColumn({ type: 'datetime', precision: 6 })
  updatedAt: Date;
}
