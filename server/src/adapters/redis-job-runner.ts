import { Inject, Injectable, Logger } from '@nestjs/common';
import { randomUUID } from 'crypto';
import type { RedisClientType } from 'redis';
import { LEADERBOARD_OPTIONS, LEADERBOARD_REDIS } from '../leaderboard.constants';
import type { ResolvedLeaderboardOptions } from '../leaderboard.options';
import type { LeaderboardJobRunner } from '../ports';
import { LeaderboardKeys } from '../util/redis-keys';

const LOCK_TTL_MS = 15 * 60_000;
const RELEASE_LUA =
  "if redis.call('GET', KEYS[1]) == ARGV[1] then return redis.call('DEL', KEYS[1]) end return 0";

/**
 * Job runner mặc định:
 *  - tiến trình được chạy job nền khi `RUN_BACKGROUND_JOBS` khác '0' (quy ước của
 *    template game-backend: chạy cluster thì chỉ worker 0 có giá trị '1');
 *  - các job nặng của BXH không chồng lên nhau nhờ một khoá Redis chung.
 *
 * Khoá này chỉ biết job của BXH. Host có khoá job nặng chung (template:
 * `runExclusiveHeavyJob`) thì nên tự cài `LeaderboardJobRunner` bằng khoá đó — xem README.
 */
@Injectable()
export class RedisJobRunner implements LeaderboardJobRunner {
  private readonly logger = new Logger('LeaderboardJobRunner');
  private readonly lockKey: string;

  constructor(
    @Inject(LEADERBOARD_REDIS) private readonly redis: RedisClientType,
    @Inject(LEADERBOARD_OPTIONS) options: ResolvedLeaderboardOptions,
  ) {
    this.lockKey = new LeaderboardKeys(options.redisKeyPrefix).lock('exclusive');
  }

  isJobRunner(): boolean {
    return process.env.RUN_BACKGROUND_JOBS !== '0';
  }

  async runExclusive(job: string, work: () => Promise<void>): Promise<boolean> {
    const token = `${job}|${process.pid}|${randomUUID()}`;
    const acquired = await this.redis.set(this.lockKey, token, { NX: true, PX: LOCK_TTL_MS });
    if (!acquired) {
      this.logger.log(`${job}: job nặng khác của BXH đang chạy, bỏ lượt`);
      return false;
    }
    try {
      await work();
      return true;
    } finally {
      await this.redis
        .eval(RELEASE_LUA, { keys: [this.lockKey], arguments: [token] })
        .catch(() => undefined);
    }
  }
}
