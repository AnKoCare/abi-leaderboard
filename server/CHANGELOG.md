# Changelog

## 0.1.0 — 22/10/2026

Bản đầu, tách từ module leaderboard của Colony Flow (prod commit `e241f8c`).

- `POST /submit`, `GET /top` (top 1.000, phân trang), `GET /rank` cho World + từng quốc gia.
- Ghi điểm qua hàng đợi Redis + worker gom lô; cache bảng hai tầng + cron dựng sẵn; tính
  hạng bằng histogram điểm.
- Điểm phụ để phân định khi bằng điểm, có reset theo mùa (mặc định tắt).
- Adapter có sẵn cho bảng `players` của template game-backend; port cho schema khác.
- Migration tạo mới hoặc nâng cấp bảng `leaderboard_entries` của template, giữ dữ liệu.

**Khác bản gốc trong Colony Flow:**
- Thứ tự xếp hạng có thêm `id` làm chốt cuối. Bản gốc trả hạng lệch với vị trí trong
  bảng khi trùng hoàn toàn: cùng điểm, cùng điểm phụ, cùng thời điểm — mọi dòng của một
  lô ghi có chung `NOW(6)`.
- `rankedAt` được so ở độ chính xác micro giây (bản gốc qua `Date` của JS chỉ còn mili giây).
- Worker flush bỏ dòng hỏng vì chính dữ liệu của nó thay vì trả cả lô về hàng đợi mãi
  (bài học sự cố 14/10/2026).
- Gom lô dở của worker đã chết bằng một tập id kèm nhịp tim, thay cho SCAN toàn keyspace.
- Dựng bảng không JOIN sang bảng người chơi: đọc top trước, nạp profile theo lô sau.
- Không còn API anonymous, không phụ thuộc League tier / Team / Royal League.
