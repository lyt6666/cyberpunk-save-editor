#!/usr/bin/env bash
#
# 赛博朋克 2077 存档修改器 —— 双击即用版（macOS）
#
# 双击这个文件会自动打开「终端」运行，找到所有手动存档并一键拉满，
# 结束后弹出一个系统对话框告诉你结果。
#
# 首次双击若提示「无法打开」，右键 → 打开 → 仍要打开。
#

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

# —— 环境：找 dotnet ——
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"

# —— 颜色 ——
GREEN='\033[0;32m'; RED='\033[0;31m'; YELLOW='\033[1;33m'; CYAN='\033[0;36m'; NC='\033[0m'
info()  { echo -e "${CYAN}[*]${NC} $*"; }
ok()    { echo -e "${GREEN}[✓]${NC} $*"; }
warn()  { echo -e "${YELLOW}[!]${NC} $*"; }
fail()  { echo -e "${RED}[✗]${NC} $*"; }

# —— 结束弹窗 ——
popup() {
    local title="$1" msg="$2"
    osascript -e "display dialog \"$msg\" with title \"$title\" buttons {\"好\"} default button \"好\"" >/dev/null 2>&1
}

# —— 清屏开始 ——
clear
echo "=============================================="
echo "  赛博朋克 2077 存档修改器（双击版）"
echo "=============================================="
echo ""

# —— 1. 确保 dotnet ——
if ! command -v dotnet >/dev/null 2>&1; then
    warn "未找到 .NET 8，正在自动安装（约需 1-2 分钟）……"
    curl -sSL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin --channel 8.0
    export PATH="$HOME/.dotnet:$PATH"
    if ! command -v dotnet >/dev/null 2>&1; then
        fail ".NET 8 安装失败。"
        popup "存档修改器" "安装 .NET 8 失败，请检查网络后重试。"
        exit 1
    fi
fi

# —— 2. 编译 ——
info "准备修改器……"
if ! dotnet build "$SCRIPT_DIR/CyberpunkSaveEditor.csproj" -c Release -v quiet >/dev/null 2>&1; then
    fail "编译失败。"
    popup "存档修改器" "修改器编译失败，请确认网络正常（需下载依赖）。"
    exit 1
fi
DLL="$SCRIPT_DIR/bin/Release/net8.0/cyberpunk-save-editor.dll"

# —— 3. 定位存档目录 ——
SAVES_DIR="$HOME/Library/Application Support/CD Projekt Red/Cyberpunk 2077/saves"
if [[ ! -d "$SAVES_DIR" ]]; then
    local_d="$(mdfind -name 'sav.dat' 2>/dev/null | head -1)"
    [[ -n "$local_d" ]] && SAVES_DIR="$(dirname "$(dirname "$local_d")")"
fi
if [[ ! -d "$SAVES_DIR" ]]; then
    fail "找不到存档目录。请先启动一次游戏生成存档。"
    popup "存档修改器" "找不到赛博朋克 2077 存档目录，请先启动一次游戏。"
    exit 1
fi
ok "存档目录：$SAVES_DIR"

# —— 4. 收集手动存档（严格 ManualSave-N，排除 .backup）——
SAVES=()
for f in "$SAVES_DIR"/ManualSave-*/sav.dat; do
    [[ -f "$f" ]] || continue
    base="$(basename "$(dirname "$f")")"
    [[ "$base" =~ ^ManualSave-[0-9]+$ ]] && SAVES+=("$f")
done

if [[ ${#SAVES[@]} -eq 0 ]]; then
    warn "没有找到手动存档（ManualSave-*）。"
    popup "存档修改器" "没有找到手动存档，请先在游戏里创建一个手动存档。"
    exit 1
fi

echo ""
info "发现 ${#SAVES[@]} 个手动存档，开始一键拉满："
echo ""

# —— 5. 逐个备份 + 修改 ——
SUCCESS=0; FAILED=0
for s in "${SAVES[@]}"; do
    info "处理：$(basename "$(dirname "$s")")"
    cp "$s" "${s}.backup-$(date +%Y%m%d-%H%M%S)X" && ok "已备份"
    if dotnet "$DLL" --save "$s" --max-all >/dev/null 2>&1; then
        ok "修改成功"
        SUCCESS=$((SUCCESS+1))
    else
        fail "修改失败（原档已备份可还原）"
        FAILED=$((FAILED+1))
    fi
    echo ""
done

# —— 6. 汇总 + 弹窗 ——
echo "=============================================="
ok "完成：成功 $SUCCESS 个，失败 $FAILED 个"
echo "=============================================="
warn "请完全退出游戏后重新进入，加载手动存档即可看到全部拉满。"

if [[ $FAILED -eq 0 ]]; then
    popup "赛博朋克 2077 修改完成" "已成功修改 $SUCCESS 个手动存档。请关闭游戏后重新进入，加载存档查看效果。"
else
    popup "赛博朋克 2077 修改完成" "成功 $SUCCESS 个，失败 $FAILED 个。失败的存档已自动备份，可还原。"
fi
