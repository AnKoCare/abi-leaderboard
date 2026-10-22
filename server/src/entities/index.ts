import { LeaderboardBlockedUid } from './leaderboard-blocked-uid.entity';
import { LeaderboardEntry } from './leaderboard-entry.entity';

export { LeaderboardBlockedUid, LeaderboardEntry };

/**
 * Thêm vào `entities` của TypeORM nếu host KHÔNG bật `autoLoadEntities`
 * (template có bật — khi đó không cần làm gì).
 */
export const LEADERBOARD_ENTITIES = [LeaderboardEntry, LeaderboardBlockedUid];
