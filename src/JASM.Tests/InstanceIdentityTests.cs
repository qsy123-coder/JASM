using GIMI_ModManager.Core.Helpers;

namespace JASM.Tests;

/// <summary>
/// Covers <see cref="InstanceIdentity"/> — 启动时那个同名进程是不是「自己」。
///
/// 两个方向都要钉：判成「自己」而其实是别的安装 → 把对方窗口拉前台、自己静默退出（用户看到的 2.29.0/2.29.1 串台就是这个）；
/// 判成「别人」而其实是自己 → 重复启动的用户从「窗口被拉到前台」变成「弹窗说另一个 JASM 在跑」（体验退化）。
/// 所以「读不到路径」这类不确定的情形一律按「自己」断言。
/// </summary>
public class InstanceIdentityTests
{
    private const string OwnPath = @"D:\BaiduNetdiskDownload\JASM_Manger\JASM\output\JASM\JASM.exe";

    [Fact]
    public void TheSamePathIsTheSameApp()
    {
        Assert.True(InstanceIdentity.IsSameApp(OwnPath, OwnPath));
    }

    [Fact]
    public void CasingDifferencesDoNotMakeItAnotherApp()
    {
        // 同一个文件，一个从资源管理器点开（盘符大写）、一个从快捷方式起（全小写）—— 是同一个安装
        Assert.True(InstanceIdentity.IsSameApp(OwnPath, OwnPath.ToLowerInvariant()));
        Assert.True(InstanceIdentity.IsSameApp(OwnPath, @"d:\baidunetdiskdownload\JASM_Manger\JASM\OUTPUT\JASM\JASM.exe"));
    }

    [Fact]
    public void TheSameExeNameInAnotherFolderIsAnotherApp()
    {
        // 实机现场：本程序在 output\ 下，D:\JASM\ 是另一个 fork 的安装，两边 exe 都叫 JASM.exe。
        // 这一条判错就是用户报的那个毛病（启动新版却停在旧版界面上）
        Assert.False(InstanceIdentity.IsSameApp(OwnPath, @"D:\JASM\JASM.exe"));
    }

    [Fact]
    public void DotSegmentsAndMixedSeparatorsResolveToTheSameApp()
    {
        Assert.True(InstanceIdentity.IsSameApp(OwnPath, @"D:\BaiduNetdiskDownload\JASM_Manger\other\..\JASM\output/JASM\JASM.exe"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnUnreadableOtherPathIsConservativelyTreatedAsTheSameApp(string? otherPath)
    {
        // 读不到对方路径（权限不足 / 进程刚退出）→ 退回改动前的行为：拉前台 + 退出。
        // 宁可漏判成「自己」（毛病照旧、日志里有路径可查），也不给用户弹一个他没法处理的窗口
        Assert.True(InstanceIdentity.IsSameApp(OwnPath, otherPath));
    }

    [Fact]
    public void AnUnreadableOwnPathIsAlsoConservativelyTheSameApp()
    {
        Assert.True(InstanceIdentity.IsSameApp(null, @"D:\JASM\JASM.exe"));
        Assert.True(InstanceIdentity.IsSameApp("   ", @"D:\JASM\JASM.exe"));
    }

    [Fact]
    public void BothSidesUnreadableIsTheSameApp()
    {
        // 两侧都读不到：没有可比信息，不该判成「另一个安装」
        Assert.True(InstanceIdentity.IsSameApp(null, null));
    }

    [Fact]
    public void APathThatCannotBeNormalizedIsTreatedAsUnreadable()
    {
        // 归一化会抛的路径（这里用超长路径 → PathTooLongException）不能把启动路径搞崩，
        // 也不该被当成「另一个安装」—— 弹窗给不出来比不弹更糟
        var tooLong = new string('x', 40000);

        Assert.True(InstanceIdentity.IsSameApp(OwnPath, tooLong));
        Assert.True(InstanceIdentity.IsSameApp(tooLong, OwnPath));
    }
}