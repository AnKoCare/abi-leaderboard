/**
 * Integration test: chạy với MySQL 8 + Redis 7 THẬT.
 *
 *   LB_TEST_MYSQL_URL=mysql://root:test@127.0.0.1:33999/lbtest
 *   LB_TEST_REDIS_URL=redis://127.0.0.1:63999
 *
 * Xem test/integration/README.md để dựng hai container dùng một lần.
 */
module.exports = {
  moduleFileExtensions: ['js', 'json', 'ts'],
  rootDir: 'test/integration',
  testRegex: '.*\\.int-spec\\.ts$',
  transform: { '^.+\\.ts$': ['ts-jest', { tsconfig: '<rootDir>/../../tsconfig.json' }] },
  testEnvironment: 'node',
  testTimeout: 120000,
};
