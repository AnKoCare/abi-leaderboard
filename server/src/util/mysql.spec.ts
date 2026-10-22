import { isPermanentRowError, writeIsolatingBadRows } from './mysql';

function mysqlError(errno: number, code: string, message = code): Error {
  return Object.assign(new Error(message), { driverError: { errno, code } });
}

describe('writeIsolatingBadRows', () => {
  it('khoanh và bỏ đúng dòng hỏng vì dữ liệu, ghi hết phần còn lại', async () => {
    const written: number[] = [];
    const dropped: number[] = [];
    await writeIsolatingBadRows(
      [1, 2, 3, 4, 5, 6, 7],
      async (chunk) => {
        if (chunk.includes(5)) throw mysqlError(1406, 'ER_DATA_TOO_LONG');
        written.push(...chunk);
      },
      (item) => dropped.push(item),
    );
    expect(dropped).toEqual([5]);
    expect(written.sort()).toEqual([1, 2, 3, 4, 6, 7]);
  });

  it('bản ghi của player đã bị xoá (khoá ngoại 1452) bị bỏ, không chặn cả lô mãi mãi', async () => {
    const dropped: string[] = [];
    await writeIsolatingBadRows(
      ['ok', 'orphan'],
      async (chunk) => {
        if (chunk.includes('orphan')) throw mysqlError(1452, 'ER_NO_REFERENCED_ROW_2');
      },
      (item) => dropped.push(item),
    );
    expect(dropped).toEqual(['orphan']);
  });

  it('lỗi tạm thời (mất kết nối) ném ra ngay, KHÔNG bỏ dòng nào', async () => {
    const dropped: number[] = [];
    let calls = 0;
    await expect(
      writeIsolatingBadRows(
        [1, 2, 3],
        async () => {
          calls += 1;
          throw Object.assign(new Error('connect ECONNREFUSED'), { code: 'ECONNREFUSED' });
        },
        (item) => dropped.push(item),
      ),
    ).rejects.toThrow('ECONNREFUSED');
    expect(dropped).toEqual([]);
    expect(calls).toBe(1);
  });

  it('khoá vẫn hỏng tới tận một dòng thì ném ra để caller trả lô về hàng đợi', async () => {
    await expect(
      writeIsolatingBadRows(
        [1],
        async () => {
          throw mysqlError(1213, 'ER_LOCK_DEADLOCK', 'Deadlock found');
        },
        () => undefined,
      ),
    ).rejects.toThrow('Deadlock');
  }, 15000);
});

describe('isPermanentRowError', () => {
  it('nhận đúng lỗi dữ liệu, bỏ qua lỗi khác', () => {
    expect(isPermanentRowError(mysqlError(1406, 'ER_DATA_TOO_LONG'))).toBe(true);
    expect(isPermanentRowError(mysqlError(1205, 'ER_LOCK_WAIT_TIMEOUT'))).toBe(false);
    expect(isPermanentRowError(new Error('boom'))).toBe(false);
  });
});
