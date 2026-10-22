/** Token DI nội bộ của package. Host không cần đụng tới, trừ khi tự viết provider. */
export const LEADERBOARD_OPTIONS = Symbol('LEADERBOARD_OPTIONS');
export const LEADERBOARD_REDIS = Symbol('LEADERBOARD_REDIS');
export const LEADERBOARD_AUTH_GUARD = Symbol('LEADERBOARD_AUTH_GUARD');
export const LEADERBOARD_PLAYER_RESOLVER = Symbol('LEADERBOARD_PLAYER_RESOLVER');
export const LEADERBOARD_PROFILE_PROVIDER = Symbol('LEADERBOARD_PROFILE_PROVIDER');
export const LEADERBOARD_JOB_RUNNER = Symbol('LEADERBOARD_JOB_RUNNER');
export const LEADERBOARD_SEASON_PROVIDER = Symbol('LEADERBOARD_SEASON_PROVIDER');

/** Mã board toàn cầu (tab World). Không phải mã ISO nên không đụng board quốc gia nào. */
export const WORLD_BOARD = 'ww';

/** Mùa đầu tiên. Mọi dòng có từ trước khi bật điểm phụ đều thuộc mùa này. */
export const FIRST_SEASON = 1;

/** Tên bảng — giữ đúng tên của template game-backend để cài đè lên dữ liệu sẵn có. */
export const ENTRIES_TABLE = 'leaderboard_entries';
export const BLOCKED_UIDS_TABLE = 'leaderboard_blocked_uids';

/**
 * Field profile mặc định khi dùng adapter template: đủ để vẽ một dòng BXH (tên, level,
 * avatar, khung, huy hiệu) từ `players.info`. Game có field riêng thì khai báo lại qua
 * option `profile.fields` / `profile.optionalFields`.
 */
export const TEMPLATE_PROFILE_FIELDS = [
  'Level',
  'PlayerName',
  'AvatarIndex',
  'FrameIndex',
  'BadgeIndex',
] as const;
