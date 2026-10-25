#!/usr/bin/env bash
# Kiểm tra nhanh sau khi tích hợp package vào host dựng từ template game-backend.
#
#   BASE=http://localhost:3000/api/v1 ./examples/smoke-test.sh
#
# - Host phải bật cron dày (README → "Thử tay trên máy dev"), không thì bảng 20 phút mới đổi.
# - Đăng nhập bằng `POST /auth/external-login` (provider device) của template; host khác thì sửa
#   hàm `login`. Mỗi lần chạy tạo 2 tài khoản mới rồi tự xoá ở cuối — đừng chạy trên production.
# - Cần curl và jq.
set -euo pipefail

BASE="${BASE:-http://localhost:3000/api/v1}"
RUN="lb-smoke-$(date +%s)"
WAIT_SEC="${WAIT_SEC:-90}"

fail() { echo "FAIL: $*" >&2; exit 1; }
ok() { echo "  ok  $*"; }

# call METHOD PATH TOKEN [BODY] → in "<http code> <body>"
call() {
  local out
  out=$(curl -sS -o /tmp/lb-smoke-body -w '%{http_code}' -X "$1" "$BASE$2" \
    ${3:+-H "Authorization: Bearer $3"} -H 'Content-Type: application/json' ${4:+-d "$4"})
  echo "$out $(cat /tmp/lb-smoke-body)"
}
expect() { # expect CODE "<code> <body>" mô-tả
  [ "${2%% *}" = "$1" ] || fail "$3: mong $1, nhận ${2%% *} — ${2#* }"
  ok "$3"
}

login() {
  local r
  r=$(curl -sS -X POST "$BASE/auth/external-login" -H 'Content-Type: application/json' \
    -d "{\"provider\":\"device\",\"externalId\":\"$RUN-$1\"}")
  TOKEN=$(echo "$r" | jq -r '.accessToken // .data.accessToken')
  UID_=$(echo "$r" | jq -r '(.player // .data.player).uid')
  [ -n "$TOKEN" ] && [ "$TOKEN" != null ] || fail "đăng nhập $1: $r"
}

echo "== đăng nhập 2 tài khoản mới"
login a; TA=$TOKEN; UA=$UID_
login b; TB=$TOKEN; UB=$UID_
ok "a=$UA b=$UB"

echo "== đổi quốc gia trước khi có điểm (người chơi mới)"
expect 200 "$(call PATCH /player/country "$TA" '{"countryCode":"vn"}')" "a → vn"
expect 200 "$(call PATCH /player/country "$TB" '{"countryCode":"vn"}')" "b → vn"

echo "== lưu Level qua PUT /player/info (đồng bộ ngầm bằng syncScore)"
expect 200 "$(call PUT /player/info "$TA" '{"Level":12,"PlayerName":"Smoke A"}')" "a Level 12"
expect 200 "$(call PUT /player/info "$TB" '{"Level":30,"PlayerName":"Smoke B"}')" "b Level 30"
expect 200 "$(call PUT /player/info "$TB" '{"Level":5,"PlayerName":"Smoke B"}')" "b Level 5 (bảng phải giữ 30)"

echo "== client gửi điểm"
R=$(call POST /leaderboard/submit "$TA" '{"score":20}')
expect 201 "$R" "a submit 20"
[ "$(echo "${R#* }" | jq -r .countryCode)" = vn ] || fail "submit trả countryCode khác vn: ${R#* }"
expect 401 "$(call POST /leaderboard/submit '' '{"score":20}')" "submit không token → 401"
expect 400 "$(call POST /leaderboard/submit "$TA" '{"score":-1}')" "điểm âm → 400"

echo "== chờ cron dựng bảng vn (tối đa ${WAIT_SEC}s)"
deadline=$(( $(date +%s) + WAIT_SEC ))
while :; do
  TOP=$(curl -sS "$BASE/leaderboard/top?countryCode=vn")
  SA=$(echo "$TOP" | jq --arg u "$UA" '[.entries[] | select(.uid == $u)][0]')
  SB=$(echo "$TOP" | jq --arg u "$UB" '[.entries[] | select(.uid == $u)][0]')
  [ "$SA" != null ] && [ "$SB" != null ] && break
  [ "$(date +%s)" -lt "$deadline" ] || fail "sau ${WAIT_SEC}s bảng vn vẫn chưa có cả a và b — cron dày đã bật chưa? $TOP"
  sleep 5
done
[ "$(echo "$SB" | jq .score)" = 30 ] || fail "điểm của b phải là 30: $SB"
[ "$(echo "$SA" | jq .score)" = 20 ] || fail "điểm của a phải là 20: $SA"
[ "$(echo "$SB" | jq .rank)" -lt "$(echo "$SA" | jq .rank)" ] || fail "b (30) phải đứng trên a (20)"
[ "$(echo "$SB" | jq -r .info.PlayerName)" = "Smoke B" ] || fail "thiếu profile trong info: $SB"
ok "a: $(echo "$SA" | jq -c '{rank,score}')  b: $(echo "$SB" | jq -c '{rank,score}')"

echo "== hạng"
RA=$(curl -sS "$BASE/leaderboard/rank?uid=$UA&countryCode=vn")
RB=$(curl -sS "$BASE/leaderboard/rank?uid=$UB&countryCode=ww")
[ "$(echo "$RA" | jq -r .board)" = vn ] || fail "rank a ở vn: $RA"
[ "$(echo "$RB" | jq -r .score)" = 30 ] || fail "rank b ở ww: $RB"
ok "a vn #$(echo "$RA" | jq .rank)  b ww #$(echo "$RB" | jq .rank)"
expect 400 "$(call GET '/leaderboard/rank?uid=abc&countryCode=ww' '')" "uid sai định dạng → 400"

echo "== dọn: xoá 2 tài khoản thử"
expect 201 "$(call POST /users/purge-by-uid "$TA" "{\"uid\":\"$UA\"}")" "xoá a"
expect 201 "$(call POST /users/purge-by-uid "$TB" "{\"uid\":\"$UB\"}")" "xoá b"

echo "OK — package chạy đúng trên host này"
