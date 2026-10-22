import { CanActivate } from '@nestjs/common';
import { resolveLeaderboardOptions } from './leaderboard.options';

class Guard implements CanActivate {
  canActivate() {
    return true;
  }
}

const base = { redisToken: 'REDIS', authGuard: Guard };

describe('resolveLeaderboardOptions', () => {
  it('mặc định = hành vi hiện tại của Colony Flow', () => {
    const o = resolveLeaderboardOptions(base);
    expect(o.players).toBe('template');
    expect(o.routePrefix).toBe('leaderboard');
    expect(o.topLimit).toBe(1000);
    expect(o.defaultPageSize).toBe(100);
    expect(o.cacheTtlSec).toBe(3600);
    expect(o.writeBehind.enabled).toBe(true);
    expect(o.secondaryScore.enabled).toBe(false);
    expect(o.profile.fields).toEqual(['Level', 'PlayerName', 'AvatarIndex', 'FrameIndex', 'BadgeIndex']);
    expect(o.uidPattern.test('1234567890')).toBe(true);
    expect(o.uidPattern.test('0123456789')).toBe(false);
    expect(o.countryCodes.has('vn')).toBe(true);
    expect(o.cron).not.toBe(false);
  });

  it("không bao giờ coi 'ww' là một quốc gia", () => {
    const o = resolveLeaderboardOptions({ ...base, countryCodes: ['VN', 'ww', 'us'] });
    expect([...o.countryCodes].sort()).toEqual(['us', 'vn']);
  });

  it('provider tự viết thì không gắn field profile của template', () => {
    const o = resolveLeaderboardOptions({
      ...base,
      players: { resolver: Guard as any, profileProvider: Guard as any },
    });
    expect(o.profile.fields).toEqual([]);
  });

  it('chặn tên bảng có thể chèn SQL', () => {
    expect(() =>
      resolveLeaderboardOptions({ ...base, templatePlayersTable: 'players; DROP TABLE x' }),
    ).toThrow(/templatePlayersTable/);
  });

  it('chặn số không hợp lệ và thiếu option bắt buộc', () => {
    expect(() => resolveLeaderboardOptions({ ...base, topLimit: 0 })).toThrow(/topLimit/);
    expect(() => resolveLeaderboardOptions({ ...base, cacheTtlSec: 1.5 })).toThrow(/cacheTtlSec/);
    expect(() => resolveLeaderboardOptions({ authGuard: Guard } as any)).toThrow(/redisToken/);
  });

  it('trang mặc định không vượt trần board', () => {
    const o = resolveLeaderboardOptions({ ...base, topLimit: 50, defaultPageSize: 100 });
    expect(o.defaultPageSize).toBe(50);
  });
});
