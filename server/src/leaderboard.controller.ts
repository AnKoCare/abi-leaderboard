import {
  Body,
  Controller,
  Get,
  Header,
  Inject,
  Post,
  Query,
  Req,
  Type,
  UnauthorizedException,
  UseGuards,
} from '@nestjs/common';
import { ApiBearerAuth, ApiOkResponse, ApiOperation, ApiTags } from '@nestjs/swagger';
import { LeaderboardAuthGuard } from './auth/leaderboard-auth.guard';
import { RankQueryDto, SubmitScoreDto, TopQueryDto } from './dto/leaderboard.dto';
import { LEADERBOARD_OPTIONS } from './leaderboard.constants';
import type { ResolvedLeaderboardOptions } from './leaderboard.options';
import { LeaderboardService } from './services/leaderboard.service';

/**
 * Dựng controller với prefix route theo option. Phải dựng trong hàm vì `@Controller(path)`
 * được đánh giá lúc khai báo class.
 */
export function createLeaderboardController(routePrefix: string, swaggerTag: string): Type<unknown> {
  @ApiTags(swaggerTag)
  @Controller(routePrefix)
  class LeaderboardController {
    constructor(
      private readonly leaderboard: LeaderboardService,
      @Inject(LEADERBOARD_OPTIONS) private readonly options: ResolvedLeaderboardOptions,
    ) {}

    @Post('submit')
    @UseGuards(LeaderboardAuthGuard)
    @ApiBearerAuth()
    @ApiOperation({ summary: 'Gửi điểm của người chơi đang đăng nhập (chỉ tăng)' })
    async submit(@Req() req: unknown, @Body() dto: SubmitScoreDto) {
      const userId = this.options.getUserId(req);
      if (!userId) throw new UnauthorizedException();
      return this.leaderboard.submitFromRequest(String(userId), dto.score, dto.secondaryScore);
    }

    @Get('top')
    @ApiOperation({
      summary: "Top của một board ('ww' = World, mã ISO = quốc gia)",
      description:
        'Board giữ tối đa topLimit (mặc định 1000) dòng. `total` = số dòng trả về, ' +
        '`boardTotal` = tổng số dòng của board. Dữ liệu cache, trễ tối đa một nhịp dựng bảng.',
    })
    @ApiOkResponse({
      schema: {
        example: {
          countryCode: 'ww',
          total: 1,
          boardTotal: 1000,
          entries: [{ rank: 1, uid: '1234567890', score: 1800, countryCode: 'vn', info: { PlayerName: 'An' } }],
        },
      },
    })
    // Trả chuỗi JSON đã dựng sẵn nên phải tự đặt content-type — Nest coi string là text/html.
    @Header('Content-Type', 'application/json; charset=utf-8')
    async top(@Query() query: TopQueryDto): Promise<string> {
      return this.leaderboard.getTopSerialized(query.countryCode, query.limit, query.offset);
    }

    @Get('rank')
    @ApiOperation({ summary: 'Hạng của một uid trên một board; null nếu uid không có trên board đó' })
    async rank(@Query() query: RankQueryDto) {
      return this.leaderboard.getRank(query.uid, query.countryCode);
    }
  }

  return LeaderboardController;
}
