using System;
using System.Linq;
using ModularShell.PlayniteIntegration;
using Playnite.SDK.Models;

/// <summary>针对真实 SDK 模型进行映射测试，不冒充真实 Playnite 进程验收。</summary>
public static class AdapterTests
{
    /// <summary>断言违反映射或依赖边界时停止构建。</summary>
    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
        Console.WriteLine("PASS " + message);
    }
    /// <summary>验证 ID 命名空间、损坏路径回退与程序集边界。</summary>
    public static int Main()
    {
        try
        {
            var game = new Game { Name = "中文游戏", GameId = "570", IsInstalled = true, CoverImage = "cover.jpg" };
            var mapped = PlayniteSource.Map(game, p => "resolved/" + p);
            Check(mapped.ItemId == game.Id.ToString("D") && mapped.ItemId != "570", "保留 Playnite ID，不混用 Steam App ID");
            Check(mapped.Provider == "playnite" && mapped.SourceInstance == "local", "稳定内容命名空间");
            Check(mapped.Title == "中文游戏" && mapped.IsInstalled && mapped.CoverPath == "resolved/cover.jpg", "公开 SDK 展示字段映射");
            Check(PlayniteSource.Map(game, p => { throw new Exception(); }).CoverPath == null, "图片解析异常回退");
            game.Name = null;
            Check(PlayniteSource.Map(game, p => p).Title == "未命名游戏", "缺失标题回退");
            var references = typeof(PlayniteSource).Assembly.GetReferencedAssemblies();
            Check(!references.Any(r => r.Name == "Playnite" || r.Name == "Playnite.Common"), "仅引用公开 SDK");
            Check(typeof(ShellPlugin).BaseType.FullName == "Playnite.SDK.Plugins.GenericPlugin", "真实 GenericPlugin 入口类型");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
