#!/bin/bash
# Chạy test của package trong một project Unity TẠM (không đụng project game nào).
#
#   unity/tools/run_unity_tests.sh <thư-mục-tạm> <EditMode|PlayMode> [đường-dẫn-Unity] [project-lấy-UniTask]
#
# - Lần đầu dựng project tạm: manifest trỏ `file:` vào package, UniTask copy từ PackageCache
#   của một project đã có (mặc định AntFood), TMP Essential Resources copy từ project đó nếu có.
# - Unity tự sinh .meta ngay trong thư mục package — nhớ commit (cài qua git mà thiếu .meta
#   là Unity bỏ qua file).
# - PlayMode chạy CÓ graphics (không -nographics) để Canvas có kích thước màn hình thật.
set -euo pipefail

DIR="${1:?thư mục tạm}"
PLATFORM="${2:?EditMode hoặc PlayMode}"
UNITY="${3:-/Applications/Unity/Hub/Editor/6000.3.19f1/Unity.app/Contents/MacOS/Unity}"
SOURCE="${4:-$HOME/Downloads/AbiGames/AntFood}"
PACKAGE="$(cd "$(dirname "$0")/../com.abigames.leaderboard" && pwd)"

if [ ! -f "$DIR/Packages/manifest.json" ]; then
  mkdir -p "$DIR/Assets" "$DIR/Packages"
  UNITASK=$(ls -d "$SOURCE"/Library/PackageCache/com.cysharp.unitask@* | head -1)
  cp -R "$UNITASK" "$DIR/Packages/com.cysharp.unitask"
  if [ -d "$SOURCE/Assets/TextMesh Pro" ]; then
    cp -R "$SOURCE/Assets/TextMesh Pro" "$DIR/Assets/"
  fi
  cat > "$DIR/Packages/manifest.json" <<JSON
{
  "dependencies": {
    "com.abigames.leaderboard": "file:$PACKAGE",
    "com.unity.nuget.newtonsoft-json": "3.2.2",
    "com.unity.test-framework": "1.6.0",
    "com.unity.ugui": "2.0.0",
    "com.unity.modules.unitywebrequest": "1.0.0",
    "com.unity.modules.jsonserialize": "1.0.0",
    "com.unity.modules.imgui": "1.0.0",
    "com.unity.modules.ui": "1.0.0",
    "com.unity.modules.uielements": "1.0.0"
  },
  "testables": ["com.abigames.leaderboard"]
}
JSON
fi

GRAPHICS="-nographics"
[ "$PLATFORM" = "PlayMode" ] && GRAPHICS=""
RESULTS="$DIR/results-$PLATFORM.xml"
rm -f "$RESULTS"
set +e
"$UNITY" -batchmode $GRAPHICS -projectPath "$DIR" -runTests -testPlatform "$PLATFORM" \
  -testResults "$RESULTS" -logFile "$DIR/editor-$PLATFORM.log"
CODE=$?
set -e

if [ ! -f "$RESULTS" ]; then
  echo "Không có file kết quả (Unity thoát mã $CODE). Lỗi biên dịch:"
  grep -E "error CS|Scripts have compiler errors" "$DIR/editor-$PLATFORM.log" | sort -u | head -20
  exit 1
fi

python3 - "$RESULTS" <<'PY'
import sys, xml.etree.ElementTree as ET
root = ET.parse(sys.argv[1]).getroot()
print('tổng:', {k: root.get(k) for k in ['total', 'passed', 'failed', 'skipped']})
for tc in root.iter('test-case'):
    if tc.get('result') == 'Passed':
        continue
    msg = tc.find('.//message')
    st = tc.find('.//stack-trace')
    print(f"  {tc.get('result').upper()} {tc.get('classname').split('.')[-1]}.{tc.get('name')}")
    if msg is not None and msg.text:
        print('     ', msg.text.strip()[:800])
    if st is not None and st.text:
        print('     ', st.text.strip()[:500])
PY
exit $CODE
