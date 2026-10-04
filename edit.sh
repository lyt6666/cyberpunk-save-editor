#!/usr/bin/env bash
#
# 赛博朋克 2077 存档修改器 —— 一键脚本（macOS）
#
# 自动找到存档目录 → 遍历所有手动存档 → 逐个备份 + 修改 → 回读校验。
# 用户无需手动查找 sav.dat 路径。
#
# 用法：
#   ./edit.sh                 # 默认：改所有手动存档（--max-all）
#   ./edit.sh --money 999999  # 只改金钱
#   ./edit.sh --max-all --save "指定存档路径"   # 只改单个存档
#
# 说明：本脚本会自动探测存档目录；查找顺序：
#   1. 默认路径 ~/Library/Application Support/CD Projekt Red/Cyberpunk 2077/saves
#   2. Spotlight (mdfind) 全局搜索 sav.dat
#   3. find 兜底搜索
#

set -euo pipefail

# —— 环境准备：确保能找到 dotnet ——
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# —— 颜色输出 ——
RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; CYAN='\033[0;36m'; NC='\033[0m'

info()  { echo -e "${CYAN}[*]${NC} $*"; }
ok()    { echo -e "${GREEN}[✓]${NC} $*"; }
warn()  { echo -e "${YELLOW}[!]${NC} $*"; }
fail()  { echo -e "${RED}[✗]${NC} $*"; }

# —— 1. 检查 dotnet 是否可用 ——
if ! command -v dotnet >/dev/null 2>&1; then
    fail "未找到 .NET 8 SDK。正在尝试安装……"
    warn "若安装失败，请手动执行：curl -sSL https://dot.net/v1/dotnet-install.sh | bash"
    curl -sSL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin --channel 8.0
    export PATH="$HOME/.dotnet:$PATH"
    if ! command -v dotnet >/dev/null 2>&1; then
        fail ".NET 8 安装失败，请检查网络后重试。"
        exit 1
    fi
fi

# —— 2. 编译工具（幂等）——
info "准备编译修改器……"
if ! dotnet build "$SCRIPT_DIR/CyberpunkSaveEditor.csproj" -c Release -v quiet >/dev/null 2>&1; then
    fail "编译失败，请检查 .NET 环境。"
    exit 1
fi
DLL="$SCRIPT_DIR/bin/Release/net8.0/cyberpunk-save-editor.dll"
ok "修改器已就绪"

# —— 3. 解析参数：区分「脚本参数」与「传给修改器的参数」——
# 默认行为：--max-all
DOTNET_ARGS=()
SINGLE_SAVE=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --save)
            SINGLE_SAVE="$2"; shift 2;;
        --help|-h)
            cat <<'HELP'
赛博朋克 2077 存档修改器 - 一键脚本

用法：
  ./edit.sh                       默认改所有手动存档（--max-all）
  ./edit.sh --money 999999        只改金钱
  ./edit.sh --saves-dir <dir>     指定存档目录（自定义位置）
  ./edit.sh --dry-run             只预览不写回
  ./edit.sh --help                显示帮助

其余参数原样传给修改器（--max-skills / --max-level 等，见 README）。
HELP
            exit 0;;
        --saves-dir)
            SAVES_DIR="$2"; shift 2;;
        --dry-run)
            DRY_RUN=1; shift;;
        *)
            DOTNET_ARGS+=("$1"); shift;;
    esac
done

# —— 4. 定位存档目录 ——
find_saves_dir() {
    local d
    # 4a. 默认路径
    d="$HOME/Library/Application Support/CD Projekt Red/Cyberpunk 2077/saves"
    [[ -d "$d" ]] && { echo "$d"; return 0; }
    # 4b. Spotlight
    d="$(mdfind -name 'sav.dat' 2>/dev/null | head -1)"
    if [[ -n "$d" ]]; then echo "$(dirname "$(dirname "$d")")"; return 0; fi
    # 4c. find 兜底（限制搜索范围避免太慢）
    d="$(find "$HOME" -name 'sav.dat' -path '*Cyberpunk*saves*' 2>/dev/null | head -1)"
    if [[ -n "$d" ]]; then echo "$(dirname "$(dirname "$d")")"; return 0; fi
    return 1
}

if [[ -n "${SAVES_DIR:-}" ]]; then
    [[ -d "$SAVES_DIR" ]] || { fail "指定的存档目录不存在：$SAVES_DIR"; exit 1; }
    info "使用指定存档目录：$SAVES_DIR"
else
    if ! SAVES_DIR="$(find_saves_dir)"; then
        fail "找不到存档目录。请用 --saves-dir <目录> 手动指定，或先启动游戏生成存档。"
        exit 1
    fi
    ok "自动定位到存档目录：$SAVES_DIR"
fi

# —— 5. 收集要修改的存档 ——
collect_saves() {
    if [[ -n "$SINGLE_SAVE" ]]; then
        [[ -f "$SINGLE_SAVE" ]] && echo "$SINGLE_SAVE"
        return
    fi
    # 默认：所有手动存档（严格匹配 ManualSave-数字 目录名，排除 .backup-* 等后缀目录）
    local f dir base
    for f in "$SAVES_DIR"/ManualSave-*/sav.dat; do
        [[ -f "$f" ]] || continue
        dir="$(dirname "$f")"
        base="$(basename "$dir")"
        # 只接受 ManualSave-<纯数字>
        [[ "$base" =~ ^ManualSave-[0-9]+$ ]] && echo "$f"
    done
}

SAVES=()
while IFS= read -r _s; do
    [[ -n "$_s" ]] && SAVES+=("$_s")
done < <(collect_saves)
if [[ ${#SAVES[@]} -eq 0 ]]; then
    fail "未找到任何手动存档（ManualSave-*）。"
    info "可用 --save <sav.dat 路径> 指定单个存档，或 --saves-dir 指定目录。"
    exit 1
fi
info "发现 ${#SAVES[@]} 个手动存档："
for s in "${SAVES[@]}"; do echo "    - $s"; done

# 默认参数：若用户没给任何修改项，则用 --max-all
if [[ ${#DOTNET_ARGS[@]} -eq 0 ]]; then
    DOTNET_ARGS+=(--max-all)
fi

# —— 6. 逐个修改（每个都备份 + 回读校验）——
SUCCESS=0; FAILED=0
for s in "${SAVES[@]}"; do
    echo ""
    info "处理：$s"
    # 备份（dry-run 不产生备份）
    if [[ "${DRY_RUN:-0}" != "1" ]]; then
        BACKUP="${s}.backup-$(date +%Y%m%d-%H%M%S)"
        cp "$s" "$BACKUP" && ok "已备份 → $BACKUP"
    fi

    ARGS=("$s" "${DOTNET_ARGS[@]}")
    if [[ "${DRY_RUN:-0}" == "1" ]]; then ARGS+=(--dry-run); fi

    if dotnet "$DLL" --save "${ARGS[@]}"; then
        if [[ "${DRY_RUN:-0}" == "1" ]]; then
            info "（预演）未写入：$s"
        else
            ok "修改成功：$s"
        fi
        SUCCESS=$((SUCCESS+1))
    else
        fail "修改失败：$s（原档已备份，可还原）"
        FAILED=$((FAILED+1))
    fi
done

echo ""
echo "──────────────────────────────"
ok "完成：成功 $SUCCESS 个，失败 $FAILED 个"
warn "请完全退出游戏后重新进入，加载对应手动存档查看效果。"
warn "如需还原，把 .backup-* 文件改回 sav.dat 即可。"
