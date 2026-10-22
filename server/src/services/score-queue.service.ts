import { Inject, Injectable, Logger } from '@nestjs/common';
import { randomUUID } from 'crypto';
import type { RedisClientType } from 'redis';
import { LEADERBOARD_OPTIONS, LEADERBOARD_REDIS } from '../leaderboard.constants';
import type { ResolvedLeaderboardOptions } from '../leaderboard.options';
import type { ScoreWrite } from '../types';
import { LeaderboardKeys } from '../util/redis-keys';

export interface PendingScore extends ScoreWrite {
  /** Chuỗi JSON nguyên bản trong hash — trả về hàng đợi đúng y như lúc nhận. */
  raw: string;
}

/** Nhớ điểm đã xếp hàng gần nhất bao lâu (để bỏ qua lần gửi không cao hơn). */
const LAST_QUEUED_TTL_SEC = 6 * 60 * 60;
/** Worker không gửi nhịp tim quá lâu thì coi như đã chết, lô dở của nó được gom lại. */
const ALIVE_TTL_SEC = 120;
const HEARTBEAT_EVERY_MS = 30_000;

/**
 * Hàng đợi write-behind cho điểm: request chỉ ghi Redis rồi trả về, worker gom lô xuống
 * MySQL. Ở Colony Flow, ghi thẳng ~115 lần/giây làm các câu upsert chen nhau khoá
 * (threads_running 47); gom lô thì cả trăm người chỉ còn một câu.
 *
 * Hash `score-pending` giữ bản MỚI NHẤT của mỗi player. Worker không đọc-rồi-xoá mà
 * CLAIM: chuyển field sang hash in-flight riêng của tiến trình trong cùng một thao tác,
 * để nhiều container không xử lý trùng nhau.
 */
@Injectable()
export class ScoreQueueService {
  private readonly logger = new Logger('LeaderboardScoreQueue');
  private readonly keys: LeaderboardKeys;
  private readonly workerId = randomUUID();
  private cursor = '0';
  private registered = false;
  private lastHeartbeat = 0;

  /**
   * Chỉ xếp hàng khi điểm THỰC SỰ cao hơn lần xếp hàng trước — phần lớn lần gửi là gửi
   * lại đúng số cũ, ghi xuống chỉ tốn khoá và IO.
   *
   * Phải so CẢ điểm phụ và mùa: người đã max level thì score không tăng nữa, chỉ so
   * score là điểm phụ mới không bao giờ xuống DB. Sang mùa mới điểm phụ về 0 nên lần gửi
   * đầu tiên luôn THẤP hơn số đã ghi — không biết mùa thì nó bị chặn tới khi key hết hạn.
   *
   * Ba trường hợp theo mùa, khớp với ODKU của `LeaderboardStore`:
   *   - mùa đã ghi CŨ hơn / bằng: xếp hàng nếu score hoặc điểm phụ cao hơn;
   *   - mùa đã ghi MỚI hơn (request tới muộn): DB sẽ bỏ phần điểm phụ, nên chỉ đáng xếp
   *     hàng khi score tăng, và đặt mốc điểm phụ về 0 để lần gửi kế chắc chắn đi qua.
   */
  private static readonly QUEUE_LUA = `
local last = redis.call('GET', KEYS[2])
local score = tonumber(ARGV[2])
local ttl = tonumber(ARGV[3])
local sp = tonumber(ARGV[5])
local season = tonumber(ARGV[6])
if last then
  local ls, lp, lc = string.match(last, '^(%-?%d+):(%-?%d+):(%-?%d+)$')
  if ls then
    ls = tonumber(ls)
    lp = tonumber(lp)
    lc = tonumber(lc)
    if lc > season then
      if ls >= score then
        return 0
      end
      redis.call('SET', KEYS[2], score .. ':0:' .. lc, 'EX', ttl)
      redis.call('HSET', KEYS[1], ARGV[1], ARGV[4])
      return 1
    end
    if lc == season and ls >= score and lp >= sp then
      return 0
    end
  end
end
redis.call('SET', KEYS[2], score .. ':' .. sp .. ':' .. season, 'EX', ttl)
redis.call('HSET', KEYS[1], ARGV[1], ARGV[4])
return 1
`;

  /**
   * Nhận một lô về xử lý ĐỘC QUYỀN. HDEL trả 0 = tiến trình khác vừa nhận field đó.
   * Cursor HSCAN giữ giữa các lần gọi: bắt đầu lại từ 0 mỗi lần thì chỉ quét được vùng
   * đầu hash và phần còn lại không bao giờ tới lượt.
   */
  private static readonly CLAIM_LUA = `
local cursor = ARGV[1]
local want = tonumber(ARGV[2])
local out = {}
local rounds = 0
repeat
  local res = redis.call('HSCAN', KEYS[1], cursor, 'COUNT', 200)
  cursor = res[1]
  local flat = res[2]
  for i = 1, #flat, 2 do
    if redis.call('HDEL', KEYS[1], flat[i]) == 1 then
      redis.call('HSET', KEYS[2], flat[i], flat[i + 1])
      out[#out + 1] = flat[i]
      out[#out + 1] = flat[i + 1]
    end
  end
  rounds = rounds + 1
until cursor == '0' or #out >= want * 2 or rounds >= 20
return {cursor, out}
`;

  /** Trả lô về hàng đợi, TRỪ player đã gửi bản mới hơn trong lúc đó — bản mới luôn thắng. */
  private static readonly REQUEUE_LUA = `
local restored = 0
for i = 1, #ARGV, 2 do
  if redis.call('HEXISTS', KEYS[1], ARGV[i]) == 0 then
    redis.call('HSET', KEYS[1], ARGV[i], ARGV[i + 1])
    restored = restored + 1
  end
  redis.call('HDEL', KEYS[2], ARGV[i])
end
return restored
`;

  constructor(
    @Inject(LEADERBOARD_REDIS) private readonly redis: RedisClientType,
    @Inject(LEADERBOARD_OPTIONS) options: ResolvedLeaderboardOptions,
  ) {
    this.keys = new LeaderboardKeys(options.redisKeyPrefix);
  }

  /** True = đã nhận (xếp hàng, hoặc bỏ qua vì không cao hơn). False = Redis hỏng, caller tự ghi DB. */
  async enqueue(write: ScoreWrite): Promise<boolean> {
    try {
      await this.redis.eval(ScoreQueueService.QUEUE_LUA, {
        keys: [this.keys.pending(), this.keys.lastQueued(write.playerId)],
        arguments: [
          write.playerId,
          String(Math.floor(write.score)),
          String(LAST_QUEUED_TTL_SEC),
          JSON.stringify(write),
          String(Math.floor(write.secondaryScore)),
          String(Math.floor(write.season)),
        ],
      });
      return true;
    } catch (error) {
      this.logger.warn(`xếp hàng điểm lỗi: ${(error as Error).message}`);
      return false;
    }
  }

  async claim(limit: number): Promise<PendingScore[]> {
    await this.heartbeat();
    const inflight = this.keys.inflight(this.workerId);
    let cursor: string;
    let flat: string[];
    try {
      [cursor, flat] = (await this.redis.eval(ScoreQueueService.CLAIM_LUA, {
        keys: [this.keys.pending(), inflight],
        arguments: [this.cursor, String(Math.max(limit, 50))],
      })) as [string, string[]];
    } catch (error) {
      this.cursor = '0';
      this.logger.warn(`nhận lô điểm lỗi: ${(error as Error).message}`);
      return [];
    }
    this.cursor = String(cursor ?? '0');

    const out: PendingScore[] = [];
    for (let i = 0; i < flat.length; i += 2) {
      try {
        out.push({ ...(JSON.parse(flat[i + 1]) as ScoreWrite), raw: flat[i + 1] });
      } catch {
        // Field rác không bao giờ ghi được — bỏ luôn khỏi in-flight.
        await this.redis.hDel(inflight, flat[i]).catch(() => undefined);
      }
    }
    return out;
  }

  async complete(entries: PendingScore[]): Promise<void> {
    if (entries.length === 0) return;
    try {
      await this.redis.hDel(
        this.keys.inflight(this.workerId),
        entries.map((e) => e.playerId),
      );
    } catch (error) {
      this.logger.warn(`xoá lô đã ghi khỏi in-flight lỗi: ${(error as Error).message}`);
    }
  }

  async requeue(entries: PendingScore[]): Promise<void> {
    if (entries.length === 0) return;
    const args: string[] = [];
    for (const e of entries) args.push(e.playerId, e.raw);
    try {
      await this.redis.eval(ScoreQueueService.REQUEUE_LUA, {
        keys: [this.keys.pending(), this.keys.inflight(this.workerId)],
        arguments: args,
      });
    } catch (error) {
      this.logger.warn(`trả lô về hàng đợi lỗi: ${(error as Error).message}`);
    }
  }

  /** Đăng ký worker vào tập id (một lần) và gia hạn "còn sống" mỗi 30 giây. */
  async heartbeat(): Promise<void> {
    const now = Date.now();
    if (this.registered && now - this.lastHeartbeat < HEARTBEAT_EVERY_MS) return;
    try {
      if (!this.registered) {
        await this.redis.sAdd(this.keys.inflightRegistry(), this.workerId);
        this.registered = true;
      }
      await this.redis.set(this.keys.inflightAlive(this.workerId), '1', { EX: ALIVE_TTL_SEC });
      this.lastHeartbeat = now;
    } catch (error) {
      this.logger.warn(`nhịp tim worker lỗi: ${(error as Error).message}`);
    }
  }

  /**
   * Gom lô dở của worker đã chết (restart, deploy) về hàng đợi. Worker còn sống nhưng
   * lỡ nhịp tim cũng bị gom — vô hại, vì ghi trùng một lô cho cùng kết quả.
   */
  async recoverOrphans(): Promise<number> {
    let recovered = 0;
    try {
      const ids = await this.redis.sMembers(this.keys.inflightRegistry());
      for (const id of ids) {
        if (id === this.workerId) continue;
        if (await this.redis.exists(this.keys.inflightAlive(id))) continue;

        const key = this.keys.inflight(id);
        const entries = await this.redis.hGetAll(key);
        const fields = Object.keys(entries);
        if (fields.length > 0) {
          const args: string[] = [];
          for (const f of fields) args.push(f, entries[f]);
          await this.redis.eval(ScoreQueueService.REQUEUE_LUA, {
            keys: [this.keys.pending(), key],
            arguments: args,
          });
          recovered += fields.length;
        }
        await this.redis.del(key);
        await this.redis.sRem(this.keys.inflightRegistry(), id);
      }
    } catch (error) {
      this.logger.warn(`gom lô dở lỗi: ${(error as Error).message}`);
    }
    return recovered;
  }

  /** Bỏ điểm đang chờ của một player (khi xoá tài khoản). */
  async forget(playerId: string): Promise<void> {
    await this.redis.hDel(this.keys.pending(), playerId);
    await this.redis.del(this.keys.lastQueued(playerId));
  }

  async size(): Promise<number> {
    return this.redis.hLen(this.keys.pending());
  }
}
