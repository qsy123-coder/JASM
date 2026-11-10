using System.Text;
using GIMI_ModManager.Core.Entities.Mods.InGameMenu;

namespace JASM.Tests;

/// <summary>
/// 命名空间改名的文件层行为：作者 ini 里的 <c>namespace = Mods\Xin-HunSha</c>（连字符）
/// 会让菜单里的变量引用变成语法错误，生成前统一改成 ASCII 安全名；
/// 标记行记录原名，「移除菜单」时按标记还原。
/// </summary>
public class IniNamespaceRewriterTests : IDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "jasm-ns-" + Guid.NewGuid().ToString("N"));

    public IniNamespaceRewriterTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void RewritesUnsafeNamespaceAndEveryReference()
    {
        var modIni = Path.Combine(_root, "mod.ini");
        File.WriteAllText(modIni, """
            namespace = Mods\Xin-HunSha

            [KeyQunzi]
            key = VK_DOWN
            type = cycle
            $qunzi = 0,1

            [Present]
            $\Mods\Xin-HunSha\qunzi = 1
            """, Utf8NoBom);

        var guiDir = Path.Combine(_root, "GUI");
        Directory.CreateDirectory(guiDir);
        var guiIni = Path.Combine(guiDir, "ListGUI.ini");
        File.WriteAllText(guiIni, """
            namespace = Mods\Xin-LeiChong

            [CommandListInit]
            run = CommandList\Mods\Xin-HunSha\Foo
            """, Utf8NoBom);

        var rewrites = IniNamespaceRewriter.Apply(_root);

        var safeHunSha = IniNamespaceRewriter.BuildSafeNamespace(@"Mods\Xin-HunSha");
        var safeLeiChong = IniNamespaceRewriter.BuildSafeNamespace(@"Mods\Xin-LeiChong");
        Assert.Equal(2, rewrites.Count);

        var modText = File.ReadAllText(modIni);
        Assert.Contains(IniNamespaceRewriter.RenamedMarkerPrefix + @"Mods\Xin-HunSha", modText);
        Assert.Contains("namespace = " + safeHunSha, modText);
        Assert.Contains(@"$\" + safeHunSha + @"\qunzi = 1", modText);
        Assert.DoesNotContain(@"\Mods\Xin-HunSha\", modText);

        var guiText = File.ReadAllText(guiIni);
        Assert.Contains("namespace = " + safeLeiChong, guiText);
        // 命令列表引用（run = CommandList\Ns\Name）也要跟着改名
        Assert.Contains(@"run = CommandList\" + safeHunSha + @"\Foo", guiText);
    }

    [Fact]
    public void ApplyIsIdempotent()
    {
        var modIni = Path.Combine(_root, "mod.ini");
        File.WriteAllText(modIni, """
            namespace = Mods\Xin-HunSha

            [KeyQunzi]
            key = VK_DOWN
            type = cycle
            $qunzi = 0,1
            """, Utf8NoBom);

        IniNamespaceRewriter.Apply(_root);
        var afterFirst = File.ReadAllText(modIni);

        var second = IniNamespaceRewriter.Apply(_root);

        Assert.Equal(afterFirst, File.ReadAllText(modIni));
        Assert.Single(second);
        // 标记行只能有一行，重复生成不能叠加
        Assert.Single(afterFirst.Split('\n'),
            line => line.StartsWith(IniNamespaceRewriter.RenamedMarkerPrefix, StringComparison.Ordinal));
    }

    [Fact]
    public void RestoreReturnsFileToOriginalBytes()
    {
        var modIni = Path.Combine(_root, "mod.ini");
        var original = """
            namespace = Mods\Xin-HunSha

            [Constants]
            global $qunzi = 0

            [Present]
            $\Mods\Xin-HunSha\qunzi = 1
            """;
        File.WriteAllText(modIni, original, Utf8NoBom);

        IniNamespaceRewriter.Apply(_root);
        Assert.NotEqual(original, File.ReadAllText(modIni));

        Assert.True(IniNamespaceRewriter.Restore(_root));

        Assert.Equal(original, File.ReadAllText(modIni));
    }

    [Fact]
    public void ContentHashIgnoresNamespaceRewrite()
    {
        // 油光补丁按内容哈希判断「用户有没有改文件」；菜单生成改名后哈希必须不变，
        // 否则撤销油光会被误判成冲突。
        var modIni = Path.Combine(_root, "mod.ini");
        File.WriteAllText(modIni, """
            namespace = Mods\Xin-HunSha

            [KeyQunzi]
            key = VK_DOWN
            type = cycle
            $qunzi = 0,1
            """, Utf8NoBom);

        var before = IniFileText.ComputeContentHash(modIni);
        IniNamespaceRewriter.Apply(_root);

        Assert.Equal(before, IniFileText.ComputeContentHash(modIni));
    }

    [Fact]
    public void LeavesSafeNamespacesAlone()
    {
        var modIni = Path.Combine(_root, "mod.ini");
        var original = """
            namespace = FoxMain

            [KeyShoes]
            key = VK_DOWN
            type = cycle
            $xiezi = 4,8,9
            """;
        File.WriteAllText(modIni, original, Utf8NoBom);

        Assert.Empty(IniNamespaceRewriter.Apply(_root));
        Assert.Equal(original, File.ReadAllText(modIni));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}