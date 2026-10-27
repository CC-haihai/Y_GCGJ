#!/usr/bin/env bash
# 生成成品：把 Core + UI 编成一个 dll，铺到交付根目录。
#   Y_归层工具V1.0.dll   —— 就这一个文件，放进 CAD 插件目录即可
#
# 源码工程（Core / UI / TestKit）不动，测试照旧跑分离版。
# 另：源码/dist 下保留一份 AutoLayer.bundle 模板，需要 Autoloader 装法时自己用。
set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"          # AutoLayer_1.0
NAME="Y_归层工具V1.0.dll"

# 老名字，铺之前先清掉，避免目录里堆版本
OLD_NAMES=("自动归层.dll" "自动归层(QH)v1.0.dll" "自动归层(QH)v1.1.dll" "自动归层(QH)v1.2.dll" "自动归层(QH)v1.3.dll" "自动归层(QH)v1.4.dll" "自动归层(QH)v1.5.dll" "自动归层(QH)v1.6.dll" "自动归层(QH)v1.7.dll" "自动归层(QH)v1.8.dll" "自动归层(QH)v1.9.dll" "自动归层(QH)v1.10.dll" "自动归层(QH)v1.11.dll" "自动归层(QH)v1.12.dll" "自动归层(QH)v1.13.dll" "AutoLayer.Core.dll" "AutoLayer.UI.dll")

echo "==> 构建合并版"
dotnet build "$HERE/AutoLayer.Plugin/AutoLayer.Plugin.csproj" -c Release -v q --nologo || exit 1

SRC="$HERE/bin/$NAME"
[ -f "$SRC" ] || { echo "未生成 $SRC"; exit 1; }

clean() {
  local dir="$1"
  local n
  for n in "${OLD_NAMES[@]}"; do rm -f "$dir/$n"; done
}

echo "==> 铺到交付根目录"
clean "$ROOT"
rm -rf "$ROOT/NETLOAD" "$ROOT/AutoLayer.bundle"
cp "$SRC" "$ROOT/$NAME"

echo "==> 更新 dist 里的 bundle 模板（可选装法用）"
DIST="$HERE/dist/AutoLayer.bundle/Contents/Windows"
if [ -d "$HERE/dist/AutoLayer.bundle" ]; then
  mkdir -p "$DIST"
  clean "$DIST"
  cp "$SRC" "$DIST/$NAME"
fi

echo
echo "成品:"
ls -l "$ROOT/$NAME"
