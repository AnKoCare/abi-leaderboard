import { DynamicModule, Module, Provider } from '@nestjs/common';
import { TypeOrmModule } from '@nestjs/typeorm';
import { ConstantSeasonProvider } from './adapters/constant-season.provider';
import { RedisJobRunner } from './adapters/redis-job-runner';
import { TemplatePlayersAdapter } from './adapters/template-players.adapter';
import { LeaderboardAuthGuard } from './auth/leaderboard-auth.guard';
import { LeaderboardBlockedUid, LeaderboardEntry } from './entities';
import {
  LEADERBOARD_AUTH_GUARD,
  LEADERBOARD_JOB_RUNNER,
  LEADERBOARD_OPTIONS,
  LEADERBOARD_PLAYER_RESOLVER,
  LEADERBOARD_PROFILE_PROVIDER,
  LEADERBOARD_REDIS,
  LEADERBOARD_SEASON_PROVIDER,
} from './leaderboard.constants';
import { createLeaderboardController } from './leaderboard.controller';
import { LeaderboardModuleOptions, resolveLeaderboardOptions } from './leaderboard.options';
import { BoardService } from './services/board.service';
import { LeaderboardStore } from './services/leaderboard.store';
import { LeaderboardService } from './services/leaderboard.service';
import { LeaderboardMaintenanceService } from './services/maintenance.service';
import { RankService } from './services/rank.service';
import { ScoreFlushService } from './services/score-flush.service';
import { ScoreQueueService } from './services/score-queue.service';

/**
 * Bảng xếp hạng người chơi (World + theo quốc gia).
 *
 * ```ts
 * LeaderboardModule.forRoot({ redisToken: REDIS_CLIENT, authGuard: JwtAuthGuard })
 * ```
 *
 * Host cần có sẵn: TypeORM (MySQL), client node-redis v4 và `ScheduleModule.forRoot()`.
 * Xem README cho các bước cài vào template game-backend.
 */
@Module({})
export class LeaderboardModule {
  static forRoot(options: LeaderboardModuleOptions): DynamicModule {
    const resolved = resolveLeaderboardOptions(options);

    const playerProviders: Provider[] =
      resolved.players === 'template'
        ? [
            TemplatePlayersAdapter,
            { provide: LEADERBOARD_PLAYER_RESOLVER, useExisting: TemplatePlayersAdapter },
            { provide: LEADERBOARD_PROFILE_PROVIDER, useExisting: TemplatePlayersAdapter },
          ]
        : [
            { provide: LEADERBOARD_PLAYER_RESOLVER, useClass: resolved.players.resolver },
            { provide: LEADERBOARD_PROFILE_PROVIDER, useClass: resolved.players.profileProvider },
          ];

    return {
      module: LeaderboardModule,
      imports: [TypeOrmModule.forFeature([LeaderboardEntry, LeaderboardBlockedUid]), ...resolved.imports],
      controllers: [createLeaderboardController(resolved.routePrefix, resolved.swaggerTag)],
      providers: [
        { provide: LEADERBOARD_OPTIONS, useValue: resolved },
        { provide: LEADERBOARD_REDIS, useExisting: resolved.redisToken },
        { provide: LEADERBOARD_AUTH_GUARD, useClass: resolved.authGuard },
        { provide: LEADERBOARD_JOB_RUNNER, useClass: resolved.jobRunner ?? RedisJobRunner },
        {
          provide: LEADERBOARD_SEASON_PROVIDER,
          useClass: resolved.secondaryScore.seasonProvider ?? ConstantSeasonProvider,
        },
        ...playerProviders,
        LeaderboardAuthGuard,
        LeaderboardStore,
        ScoreQueueService,
        ScoreFlushService,
        BoardService,
        RankService,
        LeaderboardService,
        LeaderboardMaintenanceService,
      ],
      exports: [LeaderboardService],
    };
  }
}
