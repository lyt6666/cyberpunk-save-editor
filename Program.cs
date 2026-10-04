using System.Reflection;
using WolvenKit.RED4.Save;
using WolvenKit.RED4.Save.IO;
using WolvenKit.RED4.Types;

namespace CyberpunkSaveEditor;

/// <summary>
/// 赛博朋克 2077 存档修改器（macOS 版）
/// 基于 WolvenKit.RED4 官方存档解析库，安全地修改金钱、属性点、专长点、
/// 属性、技能熟练度、等级、街头声望、专长区域、特质。
///
/// 用法：
///   dotnet run -- --save "/path/to/ManualSave-1/sav.dat" --money 2147483647 --backup
///   dotnet run -- --save "/path/to/sav.dat" --max-all --backup
///
/// 见 PrintHelp() 查看全部参数。
/// </summary>
internal static class Program
{
    // 金钱在背包里的物品 TweakDBID hash（Items.money）
    private const ulong MoneyHash = 0x0BF5E188EC;

    // 单个属性的等级上限（2.x）
    private const int AttrMaxLevel = 20;

    // 技能熟练度满级、等级上限、街头声望上限（游戏合法硬上限，2.x）
    private const int SkillMaxLevel = 60;
    private const int LevelMax = 60;
    private const int StreetCredMax = 50;

    private static int Main(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
        {
            PrintHelp();
            return 0;
        }

        var opt = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--save":
                    opt.SavePath = args[++i];
                    break;
                case "--money":
                    opt.MoneyTarget = uint.Parse(args[++i]);
                    break;
                case "--attr-points":
                    opt.AttrPoints = int.Parse(args[++i]);
                    break;
                case "--perk-points":
                    opt.PerkPoints = int.Parse(args[++i]);
                    break;
                case "--max-attrs":
                    opt.MaxAttrs = true;
                    break;
                case "--max-skills":
                    opt.MaxSkills = true;
                    break;
                case "--max-level":
                    opt.MaxLevel = true;
                    break;
                case "--max-streetcred":
                    opt.MaxStreetCred = true;
                    break;
                case "--unlock-perk-areas":
                    opt.UnlockPerkAreas = true;
                    break;
                case "--unlock-traits":
                    opt.UnlockTraits = true;
                    break;
                case "--max-all":
                    opt.MaxAttrs = opt.MaxSkills = opt.MaxLevel = opt.MaxStreetCred
                        = opt.UnlockPerkAreas = opt.UnlockTraits = true;
                    break;
                case "--backup":
                    opt.Backup = true;
                    break;
                case "--dry-run":
                    opt.DryRun = true;
                    break;
                default:
                    Console.Error.WriteLine($"错误：未知参数 {args[i]}");
                    PrintHelp();
                    return 1;
            }
        }

        if (string.IsNullOrEmpty(opt.SavePath))
        {
            Console.Error.WriteLine("错误：必须提供 --save <存档 sav.dat 路径>");
            return 1;
        }

        if (!File.Exists(opt.SavePath))
        {
            Console.Error.WriteLine($"错误：找不到存档文件：{opt.SavePath}");
            return 1;
        }

        try
        {
            Run(opt);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"\n发生错误：{ex.Message}");
            return 1;
        }
    }

    private static void Run(Options opt)
    {
        Console.WriteLine($"读取存档：{opt.SavePath}");

        if (opt.Backup)
        {
            var backupPath = opt.SavePath + $".backup-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Copy(opt.SavePath, backupPath);
            Console.WriteLine($"已备份原存档：{backupPath}");
        }

        CyberpunkSaveFile save;
        using (var fs = File.OpenRead(opt.SavePath))
        using (var reader = new CyberpunkSaveReader(fs))
        {
            var res = reader.ReadFile(out save);
            if (res != EFileReadErrorCodes.NoError || save == null)
            {
                throw new InvalidDataException($"读取存档失败：{res}");
            }
        }

        Console.WriteLine($"读取成功，共 {save.Nodes.Count} 个节点");

        bool changed = false;

        // —— 修改金钱 ——
        int moneyItems = 0;
        long moneyBefore = 0;
        var invNode = save.Nodes.FirstOrDefault(n => n.Name == "inventory");
        if (invNode?.Value is Inventory inventory)
        {
            foreach (var sub in inventory.SubInventories)
            {
                foreach (var item in sub.Items)
                {
                    var id = item.ItemInfo.ItemId.Id;
                    var hashField = id.GetType().GetField("_hash", BindingFlags.NonPublic | BindingFlags.Instance);
                    var hash = (ulong)hashField!.GetValue(id);
                    if (hash == MoneyHash)
                    {
                        moneyBefore += item.Quantity;
                        item.Quantity = opt.MoneyTarget;
                        moneyItems++;
                        changed = true;
                    }
                }
            }

            if (moneyItems == 0)
                Console.WriteLine("⚠ 警告：未在背包中找到金钱物品（Items.money），金钱未修改。");
            else
                Console.WriteLine($"金钱：{moneyItems} 个物品，{moneyBefore} → 每个改成 {opt.MoneyTarget}");
        }

        // —— 修改角色成长数据（点数 / 属性 / 技能 / 等级 / 声望 / 专长区域 / 特质）——
        var container = save.Nodes.FirstOrDefault(n => n.Name == "ScriptableSystemsContainer");
        if (container?.Value is Package pkg && pkg.Content is WolvenKit.RED4.Archive.Buffer.RedPackage rp)
        {
            foreach (var chunk in rp.Chunks)
            {
                if (chunk is not RedBaseClass rbc || rbc.GetType().Name != "PlayerDevelopmentSystem")
                    continue;

                var playerDataArr = (System.Collections.IEnumerable)rbc.GetType()
                    .GetProperty("PlayerData")!.GetValue(rbc)!;
                var handle = playerDataArr.Cast<object>().First();
                var pd = handle.GetType().GetProperty("Chunk")!.GetValue(handle) as PlayerDevelopmentData;
                if (pd == null)
                    continue;

                // 1) 属性点 / 专长点
                foreach (var dp in pd.DevPoints)
                {
                    var type = dp.Type.ToString();
                    if (type == "Attribute")
                    {
                        Console.WriteLine($"属性点：Unspent {dp.Unspent} → {opt.AttrPoints}");
                        dp.Unspent = opt.AttrPoints;
                        changed = true;
                    }
                    else if (type == "Primary" || type == "Secondary")
                    {
                        Console.WriteLine($"专长点({type})：Unspent {dp.Unspent} → {opt.PerkPoints}");
                        dp.Unspent = opt.PerkPoints;
                        changed = true;
                    }
                }

                // 2) 属性拉到 20 级
                if (opt.MaxAttrs)
                {
                    foreach (var attr in pd.Attributes)
                    {
                        var name = attr.AttributeName.ToString();
                        var cur = attr.Value;
                        if (cur < AttrMaxLevel)
                        {
                            attr.Value = AttrMaxLevel;
                            Console.WriteLine($"属性 {name}：{cur} → {AttrMaxLevel}");
                            changed = true;
                        }
                    }
                }

                // 3) 技能熟练度 / 等级 / 街头声望
                if (opt.MaxSkills || opt.MaxLevel || opt.MaxStreetCred)
                {
                    foreach (var prof in pd.Proficiencies)
                    {
                        var type = prof.Type.ToString();
                        int target = SkillMaxLevel;

                        if (type == "Level")
                        {
                            if (!opt.MaxLevel) continue;
                            target = LevelMax;
                        }
                        else if (type == "StreetCred")
                        {
                            if (!opt.MaxStreetCred) continue;
                            target = StreetCredMax;
                        }
                        else
                        {
                            // 各技能熟练度
                            if (!opt.MaxSkills) continue;
                        }

                        if (prof.CurrentLevel < target)
                        {
                            Console.WriteLine($"熟练度 {type}：{prof.CurrentLevel} → {target}");
                            prof.CurrentLevel = target;
                            prof.IsAtMaxLevel = true;
                            changed = true;
                        }
                    }
                }

                // 4) 专长区域全解锁
                if (opt.UnlockPerkAreas)
                {
                    int unlocked = 0;
                    foreach (var area in pd.PerkAreas)
                    {
                        if (!area.Unlocked)
                        {
                            area.Unlocked = true;
                            unlocked++;
                        }
                    }
                    Console.WriteLine($"专长区域：解锁了 {unlocked} 个（共 {pd.PerkAreas.Count} 个）");
                    if (unlocked > 0) changed = true;
                }

                // 5) 特质全解锁
                if (opt.UnlockTraits)
                {
                    int unlocked = 0;
                    foreach (var trait in pd.Traits)
                    {
                        if (!trait.Unlocked)
                        {
                            trait.Unlocked = true;
                            unlocked++;
                        }
                    }
                    Console.WriteLine($"特质：解锁了 {unlocked} 个（共 {pd.Traits.Count} 个）");
                    if (unlocked > 0) changed = true;
                }
            }
        }
        else
        {
            Console.WriteLine("⚠ 警告：未找到 PlayerDevelopmentSystem 节点，角色成长数据未修改。");
        }

        if (!changed)
        {
            Console.WriteLine("\n没有发生任何修改（可能数值已是目标值，或未指定任何修改选项）。");
        }

        if (opt.DryRun)
        {
            Console.WriteLine("\n（--dry-run）未写入存档。");
            return;
        }

        // —— 写回（leaveOpen + 显式 Close，确保文件尾 ENOD 完整写入）——
        using (var outFs = File.Create(opt.SavePath))
        {
            using var writer = new CyberpunkSaveWriter(outFs, System.Text.Encoding.UTF8, true);
            writer.WriteFile(save);
            writer.Close();
        }

        Console.WriteLine($"\n已写回存档：{opt.SavePath}");
        Console.WriteLine("完成。请关闭游戏后重新进入并加载该存档。");
    }

    private class Options
    {
        public string SavePath = null!;
        public uint MoneyTarget = 2147483647;
        public int AttrPoints = 999;
        public int PerkPoints = 999;
        public bool MaxAttrs = false;
        public bool MaxSkills = false;
        public bool MaxLevel = false;
        public bool MaxStreetCred = false;
        public bool UnlockPerkAreas = false;
        public bool UnlockTraits = false;
        public bool Backup = false;
        public bool DryRun = false;
    }

    private static void PrintHelp()
    {
        Console.WriteLine(@"赛博朋克 2077 存档修改器（macOS 版）

用法：
  dotnet run -- --save <sav.dat 路径> [选项]

选项：
  --save <path>          存档 sav.dat 的完整路径（必需）
  --money <n>            目标金钱数额（默认 2147483647，约 21.4 亿）
  --attr-points <n>      可分配属性点数量（默认 999）
  --perk-points <n>      可分配专长点数量（默认 999）
  --max-attrs            把所有属性直接拉到 20 级
  --max-skills           把所有技能熟练度拉到 60 级
  --max-level            把角色等级拉到 60（游戏上限）
  --max-streetcred       把街头声望拉到 50（游戏上限）
  --unlock-perk-areas    解锁全部专长区域
  --unlock-traits        解锁全部特质
  --max-all              等价于上述 --max-* / --unlock-* 全部开启
  --backup               修改前先备份原存档（强烈建议）
  --dry-run              只预览改动，不写回
  --help                 显示本帮助

示例（macOS）：
  dotnet run -- --save ""$HOME/Library/Application Support/CD Projekt Red/Cyberpunk 2077/saves/ManualSave-1/sav.dat"" --max-all --backup

注意：
  * 本工具只适用于 macOS 版赛博朋克 2077（Steam macOS 版，补丁 2.x）。
  * 修改前务必 --backup 备份，改坏了可从备份一键还原。
  * 修改时请先退出游戏。
");
    }
}
