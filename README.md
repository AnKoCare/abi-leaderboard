# abi-leaderboard

Bảng xếp hạng người chơi (World + theo quốc gia) theo Level, đóng gói để dùng lại ở các game
của AbiGames, tách từ Colony Flow.

Repo: <https://github.com/AnKoCare/abi-leaderboard>

**Mới nhận package? Đọc [`HANDOVER.md`](HANDOVER.md) trước.** Trong đó có cách hệ thống chạy,
checklist tích hợp khoảng nửa ngày, những chỗ dễ sai và cách phát hành bản mới.

| Thư mục | Nội dung | Bản | Tag |
|---|---|---|---|
| [`server/`](server/README.md) | Module NestJS `@abigames/leaderboard-server` (TypeORM + MySQL + Redis) | 0.1.1 | `server-v0.1.1` |
| [`unity/com.abigames.leaderboard`](unity/com.abigames.leaderboard/README.md) | Package UPM `com.abigames.leaderboard` (core + UI uGUI, Unity 6) | 0.2.0 | `unity-v0.2.0` |

- Lịch sử thay đổi: [`server/CHANGELOG.md`](server/CHANGELOG.md),
  [`unity/com.abigames.leaderboard/CHANGELOG.md`](unity/com.abigames.leaderboard/CHANGELOG.md).
- Ví dụ tích hợp vào template game-backend:
  [`server/examples/template-integration/`](server/examples/template-integration/).
- Kiểm tra sau khi tích hợp: [`server/examples/smoke-test.sh`](server/examples/smoke-test.sh).
- Công cụ cho người phát triển package:
  - `unity/tools/compile_check.py`: biên dịch nhanh bằng dotnet.
  - `unity/tools/run_unity_tests.sh`: dựng project Unity tạm, chạy test batchmode.

Mã nguồn nội bộ — xem [`LICENSE.md`](LICENSE.md).
