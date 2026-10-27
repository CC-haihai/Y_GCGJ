#!/usr/bin/env bash
# 出整包：桌面上的 Y_归层工具V1.0.zip —— 一个包装齐「能用的」和「能看的」
#
#   Y_归层工具V1.0/
#     Y_归层工具V1.0.dll        ← 单文件成品，放进 CAD 插件目录就能用
#     安装说明.txt              ← 安装 + 用法
#     README.md                 ← 项目介绍（GitHub 首页那份）
#     LICENSE                   ← 许可
#     THIRD-PARTY-NOTICES.md    ← 第三方声明
#     源码/                     ← 完整工程（Core / UI / Plugin / TestKit / scripts）
#     演示/配置清单演示.html     ← 纯前端演示，浏览器直接打开
#
# 不含 obj / bin / out / dist —— 编译中间物和 Autoloader 模板，装的人用不到。
# 不含 演示/*.png —— 本地设计稿截图，不往外带。
# 不含 演示/B站发布文案.md、配音稿.md、Release说明.md —— 发布运营文档，只有自己用。
# 先跑 build-release.sh 出 dll，再跑这个。
set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"      # 源码
ROOT="$(cd "$HERE/.." && pwd)"                               # AutoLayer_1.0
NAME="Y_归层工具V1.0.dll"
TOP="${NAME%.dll}"
OUT="${OUT_ZIP:-$USERPROFILE/Desktop/${TOP}.zip}"

[ -f "$ROOT/$NAME" ] || { echo "缺 $ROOT/$NAME，先跑 build-release.sh"; exit 1; }
[ -f "$ROOT/安装说明.txt" ] || { echo "缺 安装说明.txt"; exit 1; }

PY="$(command -v python3 || command -v python || command -v py)"
[ -n "$PY" ] || { echo "找不到 python，无法打包"; exit 1; }

echo "==> 打包 $OUT"
"$PY" - "$ROOT" "$NAME" "$TOP" "$OUT" <<'PYEOF'
import os, sys, zipfile

root, name, top, out = sys.argv[1:5]
SKIP_DIRS = {'obj', 'bin', 'out', 'dist', '.vs', '.git'}
SKIP_EXT = {'.user', '.suo', '.cache', '.pdb', '.log', '.png'}
# 发布运营文档：只有自己看，不进 zip 也不进仓库
SKIP_FILES = {
    '演示/B站发布文案.md',
    '演示/配音稿.md',
    '演示/Release说明.md',
    '演示/演示说明.md',
    '演示/_export.py',
    '演示/演示动画.html',
    '演示/demo.ass',
}
# 视频制作素材（离屏渲染的 UI 截图），只给动画源用
SKIP_PREFIX = ('演示/assets/',)

TOP_FILES = ['安装说明.txt', 'README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md']
TOP_DIRS = ['源码', '演示']

n = 0
with zipfile.ZipFile(out, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    z.write(os.path.join(root, name), top + '/' + name); n += 1

    for f in TOP_FILES:
        p = os.path.join(root, f)
        if os.path.exists(p):
            z.write(p, top + '/' + f); n += 1

    for d in TOP_DIRS:
        base = os.path.join(root, d)
        if not os.path.isdir(base):
            continue
        for dirpath, dirnames, filenames in os.walk(base):
            dirnames[:] = sorted(x for x in dirnames if x not in SKIP_DIRS)
            for f in sorted(filenames):
                if os.path.splitext(f)[1].lower() in SKIP_EXT:
                    continue
                full = os.path.join(dirpath, f)
                rel = os.path.relpath(full, root).replace(os.sep, '/')
                if rel in SKIP_FILES or rel.startswith(SKIP_PREFIX):
                    continue
                z.write(full, top + '/' + rel)
                n += 1

print('%d 项  %.0f KB' % (n, os.path.getsize(out) / 1024.0))
PYEOF

ls -l "$OUT"
