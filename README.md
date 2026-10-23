# abi-leaderboard

Bảng xếp hạng người chơi (World + theo quốc gia) đóng gói để dùng lại ở các game của
AbiGames, tách từ Colony Flow.

| Thư mục | Nội dung | Trạng thái |
|---|---|---|
| [`server/`](server/README.md) | Module NestJS `@abigames/leaderboard-server` | 0.1.0 — xong, có unit + integration test |
| [`unity/com.abigames.leaderboard`](unity/com.abigames.leaderboard/README.md) | Package UPM `com.abigames.leaderboard` (core + UI uGUI) | 0.2.0 — xong, có test EditMode + PlayMode |

Plan đầy đủ: `docs/LEADERBOARD_PACKAGE_PLAN.md` trong repo game-backend.

Công cụ cho người phát triển package: `unity/tools/compile_check.py` (biên dịch nhanh bằng
dotnet) và `unity/tools/run_unity_tests.sh` (dựng project Unity tạm, chạy test batchmode).
