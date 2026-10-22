/**
 * App NestJS tối thiểu cài bảng xếp hạng — dùng để thử nhanh package và làm mẫu.
 * Trong project thật, import từ '@abigames/leaderboard-server' thay vì '../../../src'.
 *
 *   docker compose -f examples/host-app/docker-compose.yml up -d
 *   (tạo bảng players + chạy LEADERBOARD_MIGRATIONS, xem main.ts)
 */
import { CanActivate, ExecutionContext, Global, Injectable, Module } from '@nestjs/common';
import { ScheduleModule } from '@nestjs/schedule';
import { TypeOrmModule } from '@nestjs/typeorm';
import { createClient } from 'redis';
import { LEADERBOARD_MIGRATIONS, LeaderboardModule } from '../../../src';

export const REDIS_CLIENT = Symbol('REDIS_CLIENT');

@Global()
@Module({
  providers: [
    {
      provide: REDIS_CLIENT,
      useFactory: async () => {
        const client = createClient({ url: process.env.REDIS_URL ?? 'redis://127.0.0.1:6379' });
        await client.connect();
        return client;
      },
    },
  ],
  exports: [REDIS_CLIENT],
})
class RedisModule {}

/** CHỈ để thử: header `x-user-id` = userId. Project thật dùng JwtAuthGuard. */
@Injectable()
export class DevHeaderGuard implements CanActivate {
  canActivate(context: ExecutionContext): boolean {
    const req = context.switchToHttp().getRequest();
    const id = req.headers['x-user-id'];
    if (!id) return false;
    req.user = { id: String(id) };
    return true;
  }
}

@Module({
  imports: [
    TypeOrmModule.forRoot({
      type: 'mysql',
      url: process.env.DATABASE_URL ?? 'mysql://root:example@127.0.0.1:3306/game',
      autoLoadEntities: true,
      synchronize: false,
      migrations: LEADERBOARD_MIGRATIONS,
    }),
    RedisModule,
    ScheduleModule.forRoot(),
    LeaderboardModule.forRoot({
      redisToken: REDIS_CLIENT,
      authGuard: DevHeaderGuard,
    }),
  ],
})
export class AppModule {}
