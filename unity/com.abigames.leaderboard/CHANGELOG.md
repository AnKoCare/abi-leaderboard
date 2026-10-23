# Changelog

## 0.2.0 — 23/10/2026

Thêm phần UI (assembly `AbiGames.Leaderboard.UI`, uGUI + TextMeshPro). Từ bản này cần **Unity 6**.

- `LeaderboardVirtualList`: danh sách cuộn ảo tách từ AntFood (chỉ còn chế độ người chơi). Nối
  thêm dữ liệu (trang đầu → đủ bảng) không vẽ lại các ô đang hiện; thêm `ScrollToIndex`.
- `LeaderboardCellBase` + `DefaultLeaderboardCell`: game kế thừa ô gốc để vẽ theo phong cách riêng.
- `LeaderboardBoardView`: một bảng + ô "hạng của tôi" (người ngoài top dùng `SelfProfileProvider`)
  + trạng thái đang tải / trống / lỗi / dữ liệu cache.
- `LeaderboardTabsView`: tab World / Country / board cố định, có điều kiện khoá.
- `LeaderboardScrollJumpButtons`: thêm nút "tới hạng của tôi".
- `LeaderboardAvatarSet`, `LeaderboardCountryFlagSet`.
- `LeaderboardPanelFactory` + menu *Tools → AbiGames → Leaderboard → Create Sample Panel*.
- `LeaderboardRuntime`: chỗ giữ client / loader / submitter dùng chung, thay cho việc dò Zenject
  SceneContext như AntFood.
- Mẫu UI Demo; test PlayMode cho UI.

## 0.1.0 — 22/10/2026

Bản đầu: phần core, chưa có UI.

- `LeaderboardClient`: `submit` / `top` / `rank`; đọc được cả JSON thô lẫn vỏ `{ success, data }`,
  body rỗng của `/rank` = không có trên board; 401 → làm mới token, thử lại một lần.
- `LeaderboardBoardLoader`: luồng mở bảng của Colony Flow (cache → trang đầu → tải nền phần
  còn lại → hạng của tôi → ghi cache), có thời gian chờ giữa hai lần tải.
- `LeaderboardFileCache`: cache đĩa trên thread pool, ghi file tạm rồi mới thay.
- `LeaderboardScoreSubmitter`: gộp, thử lại có giãn cách, bỏ điểm khi lỗi vĩnh viễn, giữ điểm
  chưa gửi qua lần tắt game (theo uid).
- `Testing.FakeLeaderboardTransport`: server giả trong bộ nhớ.

Khác code gốc trong AntFood: không phụ thuộc TW.UGUI / R3 / Odin / Zenject / PlayerData;
cache không hiện "hạng của tôi" của tài khoản khác; tải phần sau trang đầu hỏng thì không
ghi cache bảng cụt.
