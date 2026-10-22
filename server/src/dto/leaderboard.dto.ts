import { ApiProperty } from '@nestjs/swagger';
import { Transform, Type } from 'class-transformer';
import { IsInt, IsOptional, IsString, Length, Max, Min } from 'class-validator';

const lower = ({ value }: { value: unknown }) =>
  typeof value === 'string' ? value.trim().toLowerCase() : value;

/** Body của `POST /submit`. */
export class SubmitScoreDto {
  @ApiProperty({ description: 'Điểm (Level), số nguyên ≥ 0. Server chỉ nhận nếu cao hơn điểm hiện tại.', example: 120 })
  @Type(() => Number)
  @IsInt()
  @Min(0)
  @Max(2_147_483_647)
  score: number;

  @ApiProperty({
    description: 'Điểm phụ để phân định khi bằng điểm. Server bỏ qua trừ khi bật secondaryScore.acceptFromClient.',
    required: false,
    example: 0,
  })
  @IsOptional()
  @Type(() => Number)
  @IsInt()
  @Min(0)
  @Max(2_147_483_647)
  secondaryScore?: number;
}

/** Query của `GET /top`. Danh sách mã hợp lệ và trần `limit` kiểm tra theo option ở service. */
export class TopQueryDto {
  @ApiProperty({ description: "'ww' cho World, hoặc mã ISO 3166-1 alpha-2 viết thường", example: 'ww' })
  @Transform(lower)
  @IsString()
  @Length(2, 2)
  countryCode: string;

  @ApiProperty({ description: 'Số dòng trả về. Bỏ trống = defaultPageSize (100).', required: false, example: 100 })
  @IsOptional()
  @Type(() => Number)
  @IsInt()
  @Min(1)
  @Max(10_000)
  limit?: number;

  @ApiProperty({ description: 'Bỏ qua bao nhiêu dòng đầu. Bỏ trống = 0.', required: false, example: 0 })
  @IsOptional()
  @Type(() => Number)
  @IsInt()
  @Min(0)
  @Max(9_999)
  offset?: number;
}

/** Query của `GET /rank`. Định dạng uid kiểm tra theo option `uidPattern` ở service. */
export class RankQueryDto {
  @ApiProperty({ example: '1234567890' })
  @IsString()
  @Length(1, 64)
  uid: string;

  @ApiProperty({ description: "'ww' cho World, hoặc mã ISO 3166-1 alpha-2 viết thường", example: 'ww' })
  @Transform(lower)
  @IsString()
  @Length(2, 2)
  countryCode: string;
}
