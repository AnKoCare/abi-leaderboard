import { CanActivate, ExecutionContext, Inject, Injectable } from '@nestjs/common';
import type { Observable } from 'rxjs';
import { LEADERBOARD_AUTH_GUARD } from '../leaderboard.constants';

/**
 * Guard của route submit: chuyển nguyên việc xác thực cho guard của host (option
 * `authGuard`). Cần lớp trung gian này vì decorator `@UseGuards` được đánh giá lúc nạp
 * file, trước khi biết host dùng guard nào.
 */
@Injectable()
export class LeaderboardAuthGuard implements CanActivate {
  constructor(@Inject(LEADERBOARD_AUTH_GUARD) private readonly inner: CanActivate) {}

  canActivate(context: ExecutionContext): boolean | Promise<boolean> | Observable<boolean> {
    return this.inner.canActivate(context);
  }
}
