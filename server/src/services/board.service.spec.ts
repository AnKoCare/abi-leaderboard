import { buildProfileInfo, currentSecondary } from './board.service';

describe('buildProfileInfo', () => {
  const opts = {
    fields: ['PlayerName', 'Level'],
    optionalFields: ['Wins', 'TeamId'],
    defaults: { PlayerName: 'Player', Level: 1 },
  };

  it('chỉ gồm key đã khai báo — không lọt field lạ của profile ra API công khai', () => {
    const info = buildProfileInfo({ PlayerName: 'An', Level: 9, Email: 'x@y.z', Wins: 3 }, opts);
    expect(info).toEqual({ PlayerName: 'An', Level: 9, Wins: 3 });
  });

  it('field bắt buộc thiếu thì lấy default, không có default thì null', () => {
    expect(buildProfileInfo({}, opts)).toEqual({ PlayerName: 'Player', Level: 1 });
    expect(buildProfileInfo({}, { ...opts, defaults: {} })).toEqual({ PlayerName: null, Level: null });
  });

  it('field tuỳ chọn bằng 0 / rỗng / false thì vắng mặt', () => {
    const info = buildProfileInfo({ PlayerName: 'An', Level: 2, Wins: 0, TeamId: '' }, opts);
    expect(info).not.toHaveProperty('Wins');
    expect(info).not.toHaveProperty('TeamId');
  });

  it('player không có profile vẫn lên bảng với giá trị mặc định', () => {
    expect(buildProfileInfo(undefined, opts)).toEqual({ PlayerName: 'Player', Level: 1 });
  });
});

describe('currentSecondary', () => {
  it('điểm của mùa đã qua coi như 0', () => {
    expect(currentSecondary({ secondaryScore: 7, season: 1 }, 2)).toBe(0);
  });
  it('cùng mùa hoặc mùa mới hơn thì giữ', () => {
    expect(currentSecondary({ secondaryScore: 7, season: 2 }, 2)).toBe(7);
    expect(currentSecondary({ secondaryScore: 7, season: 3 }, 2)).toBe(7);
  });
  it('giá trị rác về 0', () => {
    expect(currentSecondary({ secondaryScore: -4, season: 1 }, 1)).toBe(0);
  });
});
