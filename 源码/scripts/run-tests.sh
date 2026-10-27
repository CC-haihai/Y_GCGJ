#!/usr/bin/env bash
# AutoLayer 归层引擎无人值守测试：构建 -> ASCII 暂存 -> 无界面内核 -> 解码日志
set -uo pipefail

CAD_DIR="${CAD_DIR:-/c/Program Files/Autodesk/AutoCAD 2021}"
ACAD="$CAD_DIR/accoreconsole.exe"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# accoreconsole 不认非 ASCII 路径，必须用纯英文暂存目录。
# 默认放 C:/cadkit/_stage_al，自己的机器上想换就设环境变量 STAGE / STAGE_WIN。
STAGE="${STAGE:-/c/cadkit/_stage_al}"
STAGE_WIN="${STAGE_WIN:-C:/cadkit/_stage_al}"
TIMEOUT_SEC="${CAD_TIMEOUT:-240}"

[ -f "$ACAD" ] || { echo "未找到 accoreconsole: $ACAD"; exit 2; }

echo "==> [1/5] 构建"
dotnet build "$HERE/AutoLayer.Core/AutoLayer.Core.csproj" -c Release -v q --nologo || exit 1
dotnet build "$HERE/AutoLayer.UI/AutoLayer.UI.csproj" -c Release -v q --nologo || exit 1
dotnet build "$HERE/AutoLayer.TestKit/AutoLayer.TestKit.csproj" -c Release -v q --nologo || exit 1

echo "==> [2/5] 暂存到 ASCII 路径: $STAGE"
rm -rf "$STAGE"; mkdir -p "$STAGE"
cp "$HERE/bin/AutoLayer.Core.dll" "$STAGE/"
cp "$HERE/bin/AutoLayer.TestKit.dll" "$STAGE/"

echo "==> [3/5] 生成内核脚本"
printf '(setvar "SECURELOAD" 0)\n(command "_.NETLOAD" "%s/AutoLayer.Core.dll")\n(command "_.NETLOAD" "%s/AutoLayer.TestKit.dll")\nALTEST\nQUIT\nY\n' \
  "$STAGE_WIN" "$STAGE_WIN" > "$STAGE/TestRun.scr"

echo "==> [4/5] 启动无界面内核 (超时 ${TIMEOUT_SEC}s)"
cd "$STAGE" || exit 1
MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*' \
  timeout "$TIMEOUT_SEC" "$ACAD" /s "$STAGE_WIN/TestRun.scr" > "$STAGE/run.log" 2>&1
RC=$?
echo "    exit=$RC"

echo "==> [5/5] 解码日志"
mkdir -p "$HERE/out"
if iconv -f UTF-16LE -t UTF-8 "$STAGE/run.log" 2>/dev/null | tr -d '\r' > "$HERE/out/run.log"; then
  echo "    -> $HERE/out/run.log"
else
  cp "$STAGE/run.log" "$HERE/out/run.log"
fi

echo
echo "================ [ALTEST] 摘要 ================"
grep -a '\[ALTEST\]' "$HERE/out/run.log" | sed 's/^[[:space:]]*//' || echo "(无输出)"
echo "=============================================="

if grep -aq '\[ALTEST\] total=' "$HERE/out/run.log" && ! grep -aq 'failed=[1-9]' "$HERE/out/run.log"; then
  echo "结论: 全部通过"
  exit 0
fi
echo "结论: 存在失败项"
[ "$RC" -eq 124 ] && echo "注意: 内核超时被杀"
exit 1
