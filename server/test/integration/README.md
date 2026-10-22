# Integration test

Chạy trên MySQL 8 + Redis 7 THẬT, dùng hai container chỉ để test (dữ liệu nằm trên
tmpfs, tắt là mất). Mỗi lần chạy tự tạo database tên ngẫu nhiên rồi xoá; Redis bị
`FLUSHDB` — **đừng trỏ vào Redis đang dùng thật**.

```bash
docker run -d --rm --name abilb-mysql -e MYSQL_ROOT_PASSWORD=test \
  -p 127.0.0.1:33999:3306 --tmpfs /var/lib/mysql mysql:8.0
docker run -d --rm --name abilb-redis -p 127.0.0.1:63999:6379 redis:7-alpine

# chờ MySQL sẵn sàng
until docker exec abilb-mysql mysqladmin ping -ptest --silent; do sleep 2; done

LB_TEST_MYSQL_URL=mysql://root:test@127.0.0.1:33999/lbtest \
LB_TEST_REDIS_URL=redis://127.0.0.1:63999 \
npm run test:integration

docker stop abilb-mysql abilb-redis
```

Các nhóm test:

- **migration:** DB trống; chạy lại nhiều lần; nâng cấp bảng của template cũ (giữ dữ
  liệu, thêm cột / index, giữ khoá ngoại cũ) và nhận diện lỗi khoá ngoại 1452 là lỗi
  dữ liệu.
- **câu upsert:** điểm chỉ tăng, `scoreUpdatedAt`, ba trường hợp mùa của điểm phụ,
  `rankedAt` khi reset.
- **hàng đợi + flush:** gửi lộn xộn vẫn giữ điểm cao nhất; dòng hỏng bị bỏ mà không
  kẹt hàng đợi; uid bị cấm.
- **hạng khớp vị trí:** 4.000 dòng với rất nhiều dòng trùng hoàn toàn. Hạng (đường
  histogram và đường đếm thẳng) phải bằng đúng vị trí trong bảng, cho cả World lẫn
  từng quốc gia. Kèm EXPLAIN xác nhận thứ tự vẫn đi trên index.
- **HTTP:** submit → flush → dựng bảng → `top` / `rank`, kèm các request sai.
- **sang mùa mới:** reset điểm phụ của mùa cũ.
