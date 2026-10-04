using System.Reflection;
using WolvenKit.RED4.Save;
using WolvenKit.RED4.Save.IO;
using WolvenKit.RED4.Types;

namespace CyberpunkSaveEditor;

/// <summary>
/// 赛博朋克 2077 存档修改器（macOS 版）
/// 基于 WolvenKit.RED4 官方存档解析库，安全地修改金钱、属性点、专长点。
///
/// 用法：
///   dotnet run -- --save "/path/to/ManualSave-1/sav.dat" --money 2147483647 --attr-points 999 --perk-points 999 --backup
///
/// 参数：
///   --save <path>        存档 sav.dat 的完整路径（必需）
///   --money <n>          目标金钱数额（默认 2147483647，约 21.4 亿 / int32 安全上限）
///   --attr-points <n>    可分配的属性点数量（默认 999）
///   --perk-points <n>    可分配的专长点数量（默认 999）
///   --max-attrs          把所有属性直接拉到 20 级（可选）
///   --backup             修改前先备份原存档（强烈建议）
///   --dry-run            只预览改动，不写回
///   --help               显示帮助
/// </summary>
internal static class Program
{
    // 金钱在背包里的物品 TweakDBID hash（Items.money）
    private const ulong MoneyHash = 0x0BF5E188EC;

    // 属性点/专长点在 PlayerDevelopmentData.DevPoints 里的 Type
    // Attribute = 属性点，Primary/Secondary = 专长点
    private const int AttrMaxLevel = 20; // 2.x 单个属性的等级上限

    private static int Main(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
        {
            PrintHelp();
            return 0;
        }

        string savePath = null;
        uint moneyTarget = 2147483647;
        int attrPoints = 999;
        int perkPoints = 999;
        bool maxAttrs = false;
        bool backup = false;
        bool dryRun = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--save":
                    savePath = args[++i];
                    break;
                case "--money":
                    moneyTarget = uint.Parse(args[++i]);
                    break;
                case "--attr-points":
                    attrPoints = int.Parse(args[++i]);
                    break;
                case "--perk-points":
                    perkPoints = int.Parse(args[++i]);
                    break;
                case "--max-attrs":
                    maxAttrs = true;
                    break;
                case "--backup":
                    backup = true;
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
            }
        }

        if (string.IsNullOrEmpty(savePath))
        {
            Console.Error.WriteLine("错误：必须提供 --save <存档 sav.dat 路径>");
            return 1;
        }

        if (!File.Exists(savePath))
        {
            Console.Error.WriteLine($"错误：找不到存档文件：{savePath}");
            return 1;
        }

        try
        {
            Run(savePath, moneyTarget, attrPoints, perkPoints, maxAttrs, backup, dryRun);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"\n发生错误：{ex.Message}");
            return 1;
        }
    }

    private static void Run(string savePath, uint moneyTarget, int attrPoints, int perkPoints,
        bool maxAttrs, bool backup, bool dryRun)
    {
        Console.WriteLine($"读取存档：{savePath}");

        if (backup)
        {
            var backupPath = savePath + $".backup-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Copy(savePath, backupPath);
            Console.WriteLine($"已备份原存档：{backupPath}");
        }

        CyberpunkSaveFile save;
        using (var fs = File.OpenRead(savePath))
        using (var reader = new CyberpunkSaveReader(fs))
        {
            var res = reader.ReadFile(out save);
            if (res != EFileReadErrorCodes.NoError || save == null)
            {
                throw new InvalidDataException($"读取存档失败：{res}");
            }
        }

        Console.WriteLine($"读取成功，共 {save.Nodes.Count} 个节点");

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
                        item.Quantity = moneyTarget;
                        moneyItems++;
                    }
                }
            }

            if (moneyItems == 0)
            {
                Console.WriteLine("⚠ 警告：未在背包中找到金钱物品（Items.money），金钱未修改。");
            }
            else
            {
                Console.WriteLine($"金钱：{moneyItems} 个物品，{moneyBefore} → 每个改成 {moneyTarget}");
            }
        }

        // —— 修改属性点 / 专长点 ——
        var container = save.Nodes.FirstOrDefault(n => n.Name == "ScriptableSystemsContainer");
        if (container?.Value is Package pkg && pkg.Content is WolvenKit.RED4.Archive.Buffer.RedPackage rp)
        {
            foreach (var chunk in rp.Chunks)
            {
                if (chunk is not RedBaseClass rbc || rbc.GetType().Name != "PlayerDevelopmentSystem")
                {
                    continue;
                }

                var playerDataArr = (System.Collections.IEnumerable)rbc.GetType()
                    .GetProperty("PlayerData")!.GetValue(rbc)!;
                var handle = playerDataArr.Cast<object>().First();
                var pd = handle.GetType().GetProperty("Chunk")!.GetValue(handle) as PlayerDevelopmentData;
                if (pd == null)
                {
                    continue;
                }

                // 改点数
                foreach (var dp in pd.DevPoints)
                {
                    var type = dp.Type.ToString();
                    if (type == "Attribute")
                    {
                        Console.WriteLine($"属性点：Unspent {dp.Unspent} → {attrPoints}");
                        dp.Unspent = attrPoints;
                    }
                    else if (type == "Primary" || type == "Secondary")
                    {
                        Console.WriteLine($"专长点({type})：Unspent {dp.Unspent} → {perkPoints}");
                        dp.Unspent = perkPoints;
                    }
                }

                // 可选：把所有属性拉到 20 级
                if (maxAttrs)
                {
                    foreach (var attr in pd.Attributes)
                    {
                        var valProp = attr.GetType().GetProperty("Value");
                        var name = attr.GetType().GetProperty("AttributeName")?.GetValue(attr)?.ToString();
                        var cur = (int)valProp!.GetValue(attr)!;
                        if (cur < AttrMaxLevel)
                        {
                            valProp.SetValue(attr, AttrMaxLevel);
                            Console.WriteLine($"属性 {name}：{cur} → {AttrMaxLevel}");
                        }
                    }
                }
            }
        }
        else
        {
            Console.WriteLine("⚠ 警告：未找到 PlayerDevelopmentSystem 节点，属性点/专长点未修改。");
        }

        if (dryRun)
        {
            Console.WriteLine("\n（--dry-run）未写入存档。");
            return;
        }

        // —— 写回（leaveOpen + 显式 Close，确保文件尾 ENOD 完整写入）——
        using (var outFs = File.Create(savePath))
        {
            using var writer = new CyberpunkSaveWriter(outFs, System.Text.Encoding.UTF8, true);
            writer.WriteFile(save);
            writer.Close();
        }

        Console.WriteLine($"\n已写回存档：{savePath}");
        Console.WriteLine("完成。请关闭游戏后重新进入并加载该存档。");
    }

    private static void PrintHelp()
    {
        Console.WriteLine(@"赛博朋克 2077 存档修改器（macOS 版）

用法：
  dotnet run -- --save <sav.dat 路径> [选项]

选项：
  --save <path>        存档 sav.dat 的完整路径（必需）
  --money <n>          目标金钱数额（默认 2147483647）
  --attr-points <n>    可分配属性点数量（默认 999）
  --perk-points <n>    可分配专长点数量（默认 999）
  --max-attrs          把所有属性直接拉到 20 级
  --backup             修改前先备份原存档（强烈建议）
  --dry-run            只预览改动，不写回
  --help               显示本帮助

示例（macOS）：
  dotnet run -- --save ""$HOME/Library/Application Support/CD Projekt Red/Cyberpunk 2077/saves/ManualSave-1/sav.dat"" --money 2147483647 --attr-points 999 --perk-points 999 --backup

注意：
  * 本工具只适用于 macOS 版赛博朋克 2077（Steam macOS 版，补丁 2.x）。
  * 修改前务必 --backup 备份，改坏了可从备份一键还原。
  * 修改时请先退出游戏。
");
    }
}
