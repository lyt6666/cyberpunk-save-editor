# Cyberpunk 2077 存档修改器（macOS 版）

一个 **macOS 专用**的赛博朋克 2077 存档修改工具，基于官方认可的存档解析库 [WolvenKit.RED4](https://github.com/WolvenKit/WolvenKit) 编写，可以安全地修改：

- 💰 **金钱**
- 🎯 **属性点**（可分配数量）
- ⚡ **专长点**（可分配数量）
- 🏋️ **所有属性等级**（可选，直接拉到 20 级）

> ⚠️ **本工具仅适用于 macOS 版赛博朋克 2077**（Steam macOS 版，补丁 2.x）。
> Windows 用户请使用 [CyberCAT-SimpleGUI](https://github.com/Deweh/CyberCAT-SimpleGUI) 或 WolvenKit 本体。

---

## 为什么是 macOS 专用？

Windows 上一堆现成的修改器（CyberCAT-SimpleGUI、CET 控制台等），但它们要么是 `.exe` 可执行文件，要么依赖 DLL 注入机制，**都没法在 Mac 上运行**。

这个工具是纯 .NET 8 实现的命令行程序，读取 CDPR 的 RED4 存档格式（`sav.dat`）并直接改写，跨平台无 GUI 依赖，在 macOS 上开箱即用。

---

## 环境要求

- macOS（Apple Silicon 或 Intel 均可）
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

安装 .NET 8 SDK（推荐用官方安装脚本，无需 sudo）：

```bash
curl -sSL https://dot.net/v1/dotnet-install.sh | bash
export PATH="$HOME/.dotnet:$PATH"
```

---

## 使用步骤

### 1. 找到存档位置

macOS 下赛博朋克 2077 存档默认在：

```
~/Library/Application Support/CD Projekt Red/Cyberpunk 2077/saves/
```

里面有 `AutoSave-*`（自动存档）和 `ManualSave-*`（手动存档）。选择你要改的那个存档里的 **`sav.dat`** 文件。

### 2. 运行修改器

```bash
cd cyberpunk-save-editor
dotnet run -- --save "$HOME/Library/Application Support/CD Projekt Red/Cyberpunk 2077/saves/ManualSave-1/sav.dat" \
  --money 2147483647 \
  --attr-points 999 \
  --perk-points 999 \
  --backup
```

### 3. 进游戏验证

关闭游戏 → 重新进入 → 加载你修改的那个存档 → 打开角色界面即可看到效果。

---

## 参数说明

| 参数 | 说明 | 默认值 |
|------|------|--------|
| `--save <path>` | 存档 `sav.dat` 完整路径（**必需**） | — |
| `--money <n>` | 目标金钱数额 | `2147483647`（约 21.4 亿，int32 安全上限） |
| `--attr-points <n>` | 可分配属性点数量 | `999` |
| `--perk-points <n>` | 可分配专长点数量 | `999` |
| `--max-attrs` | 把所有属性直接拉到 20 级 | 关闭 |
| `--backup` | 修改前自动备份原存档 | 关闭 |
| `--dry-run` | 只预览、不写回 | 关闭 |
| `--help` | 显示帮助 | — |

---

## 技术原理

赛博朋克 2077 的 `sav.dat` 是 CDPR 专有的 **RED4 节点树格式**：

- 文件头 `VASC` magic + 版本号
- 主体是一棵节点树（字段名明文 + 二进制值，大部分未压缩）
- 一处 `FZLC`/`LZ4` 压缩块
- 尾部字符串表 + 节点索引（`ENOD` = `NODE` 倒写）

三个目标的实际存储位置（已逆向确认）：

| 目标 | 存储位置 |
|------|----------|
| 金钱 | `Inventory` 节点里 `Items.money` 物品的堆叠数量（`ItemData.Quantity`） |
| 属性点 | `ScriptableSystemsContainer` → `PlayerDevelopmentSystem` → `PlayerDevelopmentData.DevPoints[Attribute].Unspent` |
| 专长点 | 同上，`DevPoints[Primary/Secondary].Unspent` |

工具使用 `CyberpunkSaveReader` / `CyberpunkSaveWriter` 完成读改写，`NodeWriter` 会自动重算所有节点的偏移和大小，比逐字节硬改可靠得多。

---

## 免责声明

- 本工具仅供个人单机存档修改使用，请勿用于线上/竞技作弊。
- **修改前务必 `--backup` 备份**，改坏了可从 `.backup-*` 文件一键还原（改回原名 `sav.dat` 即可）。
- 本工具与 CD Projekt Red 无任何关联，存档格式随时可能随游戏更新变化。
