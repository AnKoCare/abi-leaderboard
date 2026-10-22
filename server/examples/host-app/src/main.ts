import { ValidationPipe } from '@nestjs/common';
import { NestFactory } from '@nestjs/core';
import { DataSource } from 'typeorm';
import { AppModule } from './app.module';

async function bootstrap() {
  const app = await NestFactory.create(AppModule);
  app.setGlobalPrefix('api/v1');
  app.useGlobalPipes(new ValidationPipe({ whitelist: true, forbidNonWhitelisted: true, transform: true }));

  // Chỉ cho bản thử: tạo bảng players kiểu template và chạy migration của package.
  const ds = app.get(DataSource);
  await ds.query(`
    CREATE TABLE IF NOT EXISTS players (
      id varchar(36) NOT NULL PRIMARY KEY, userId varchar(36) NOT NULL UNIQUE,
      uid varchar(10) NOT NULL UNIQUE, countryCode varchar(2) NULL, info json NULL
    ) ENGINE=InnoDB`);
  await ds.runMigrations();

  await app.listen(Number(process.env.PORT ?? 3000));
}

void bootstrap();
