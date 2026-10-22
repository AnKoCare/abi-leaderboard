import { Injectable } from '@nestjs/common';
import { FIRST_SEASON } from '../leaderboard.constants';
import type { LeaderboardSeasonProvider } from '../ports';

/** Mặc định khi không có `seasonProvider`: chỉ một mùa, điểm phụ không bao giờ reset. */
@Injectable()
export class ConstantSeasonProvider implements LeaderboardSeasonProvider {
  async currentSeason(): Promise<number> {
    return FIRST_SEASON;
  }
}
