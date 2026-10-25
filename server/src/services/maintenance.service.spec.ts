import { SchedulerRegistry } from '@nestjs/schedule';
import { resolveLeaderboardOptions } from '../leaderboard.options';
import { LeaderboardMaintenanceService } from './maintenance.service';

describe('LeaderboardMaintenanceService — đăng ký cron', () => {
  let scheduler: SchedulerRegistry;

  afterEach(() => {
    for (const name of scheduler.getCronJobs().keys()) scheduler.deleteCronJob(name);
  });

  it('job vào SchedulerRegistry của @nestjs/schedule và chạy đúng lịch', async () => {
    scheduler = new SchedulerRegistry();
    const refreshHistograms = jest.fn().mockResolvedValue(1);
    const refreshAll = jest.fn().mockResolvedValue(1);
    const options = resolveLeaderboardOptions({
      redisToken: 'REDIS',
      authGuard: class {
        canActivate() {
          return true;
        }
      },
      cron: { topBoards: '* * * * * *', histograms: '* * * * * *' },
    });
    const service = new LeaderboardMaintenanceService(
      scheduler,
      { refreshAll } as any,
      { refreshHistograms } as any,
      {} as any,
      { set: jest.fn().mockResolvedValue('OK'), del: jest.fn().mockResolvedValue(1) } as any,
      options,
      {
        isJobRunner: () => true,
        runExclusive: async (_job: string, work: () => Promise<void>) => {
          await work();
          return true;
        },
      },
      { currentSeason: async () => 1 },
    );

    service.onModuleInit();
    expect([...scheduler.getCronJobs().keys()].sort()).toEqual([
      'abi-leaderboard-histograms',
      'abi-leaderboard-top-boards',
    ]);

    await new Promise((resolve) => setTimeout(resolve, 2200));
    expect(refreshHistograms).toHaveBeenCalled();
    expect(refreshAll).toHaveBeenCalled();
  });
});
