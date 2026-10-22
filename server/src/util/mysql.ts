import type { QueryRunner } from 'typeorm';

const LOCK_ERRNOS = new Set([1205, 1213]);
const LOCK_CODES = new Set(['ER_LOCK_WAIT_TIMEOUT', 'ER_LOCK_DEADLOCK']);

/**
 * Lỗi do chính DỮ LIỆU của một dòng: ghi lại bao nhiêu lần cũng hỏng y như vậy.
 *
 * Worker flush phải bỏ hẳn dòng như thế thay vì trả về hàng đợi. Colony Flow từng dính
 * đúng lỗi này (14/10/2026): một bản ghi của player đã bị xoá (khoá ngoại 1452) bị trả
 * về hàng đợi 68.000 lần trong 50 giờ, lần nào cũng kéo cả lô xuống đường chậm.
 *
 * CHỈ liệt kê lỗi chắc chắn thuộc về dữ liệu. Mất kết nối, hết pool, timeout… là lỗi
 * tạm thời — xếp chúng vào đây thì một lần DB chập chờn sẽ xoá sạch hàng đợi.
 */
const PERMANENT_ROW_ERRNOS = new Set([
  1048, // ER_BAD_NULL_ERROR
  1264, // ER_WARN_DATA_OUT_OF_RANGE
  1292, // ER_TRUNCATED_WRONG_VALUE
  1366, // ER_TRUNCATED_WRONG_VALUE_FOR_FIELD
  1406, // ER_DATA_TOO_LONG
  1452, // ER_NO_REFERENCED_ROW_2 — bảng cũ của template còn khoá ngoại sang `players`
  3819, // ER_CHECK_CONSTRAINT_VIOLATED
]);

function driverInfo(error: unknown): { code?: string; errno?: number } {
  const e = error as
    | { driverError?: { code?: string; errno?: number }; code?: string; errno?: number }
    | null
    | undefined;
  return {
    code: e?.driverError?.code ?? e?.code,
    errno: e?.driverError?.errno ?? e?.errno,
  };
}

export function isMysqlLockError(error: unknown): boolean {
  const message = error instanceof Error ? error.message : String(error ?? '');
  if (message.includes('Deadlock') || message.includes('Lock wait timeout')) return true;
  const { code, errno } = driverInfo(error);
  return (
    (typeof code === 'string' && LOCK_CODES.has(code)) ||
    (typeof errno === 'number' && LOCK_ERRNOS.has(errno))
  );
}

export function isPermanentRowError(error: unknown): boolean {
  const { errno } = driverInfo(error);
  return typeof errno === 'number' && PERMANENT_ROW_ERRNOS.has(errno);
}

/** ER_KEY_DOES_NOT_EXITS — FORCE INDEX trỏ vào index không có (DB dựng tay). */
export function isMissingIndexError(error: unknown): boolean {
  return driverInfo(error).errno === 1176;
}

/** Phiên ghi theo lô: chờ khoá tối đa vài giây rồi thử lại, thay vì treo 50 giây mặc định. */
export async function applyLockWaitTimeout(runner: QueryRunner, seconds = 5): Promise<void> {
  await runner.query(`SET SESSION innodb_lock_wait_timeout = ${Math.max(1, Math.floor(seconds))}`);
}

export async function runWithLockRetry<T>(
  fn: () => Promise<T>,
  retries = 5,
  baseDelayMs = 40,
): Promise<T> {
  for (let attempt = 0; ; attempt += 1) {
    try {
      return await fn();
    } catch (error) {
      if (!isMysqlLockError(error) || attempt >= retries) throw error;
      const delay = baseDelayMs * 2 ** attempt + Math.floor(Math.random() * 50);
      await new Promise((resolve) => setTimeout(resolve, delay));
    }
  }
}

/**
 * Ghi một lô; hỏng thì chia đôi tới khi khoanh được dòng hỏng.
 *
 * - Khoá (deadlock / lock wait): thử lại có giãn cách; vẫn hỏng thì chia đôi — lô nhỏ
 *   giữ ít khoá hơn. Hỏng tới tận một dòng thì NÉM ra để caller trả lô về hàng đợi.
 * - Lỗi dữ liệu (`isPermanentRowError`): chia đôi tới một dòng rồi bỏ dòng đó qua
 *   `onDropped`; các dòng còn lại vẫn được ghi.
 * - Lỗi khác (mất kết nối…): ném ra ngay, không chia, không bỏ gì.
 *
 * Phần đã ghi trước khi ném ra sẽ được ghi lại ở lượt sau — câu upsert chạy lại cho
 * cùng kết quả nên không sao.
 */
export async function writeIsolatingBadRows<T>(
  items: T[],
  write: (chunk: T[]) => Promise<void>,
  onDropped: (item: T, error: unknown) => void,
): Promise<void> {
  if (items.length === 0) return;
  try {
    await runWithLockRetry(() => write(items));
  } catch (error) {
    const lock = isMysqlLockError(error);
    if (!lock && !isPermanentRowError(error)) throw error;
    if (items.length === 1) {
      if (lock) throw error;
      onDropped(items[0], error);
      return;
    }
    const mid = Math.ceil(items.length / 2);
    await writeIsolatingBadRows(items.slice(0, mid), write, onDropped);
    await writeIsolatingBadRows(items.slice(mid), write, onDropped);
  }
}

/** Nhường event loop cho request đang chờ — job nặng chạy chung tiến trình với API. */
export function yieldToEventLoop(): Promise<void> {
  return new Promise((resolve) => setImmediate(resolve));
}
