using GIMI_ModManager.Core.Helpers;

namespace JASM.Tests;

/// <summary>
/// Covers <see cref="ElevatorProvisioning"/> — the rules that decide whether the main app has to write the
/// embedded <c>Elevator.exe</c> out to disk, and which of the two copies to use afterwards.
///
/// These matter because the two copies can disagree: a single-exe user's folder may still hold a 1.0.0.0
/// helper left behind by an old folder-version install, and a naive "sibling file wins" probe would keep
/// using that useless copy even after a fresh 3.0.0.0 one has been extracted next to the user's profile.
/// </summary>
public class ElevatorProvisioningTests
{
    /// <summary>当前主程序版本（测试里当常量用，标记比对是纯字符串相等）。</summary>
    private const string CurrentVersion = "2.27.1";

    [Fact]
    public void EmbeddedResourceNameMatchesTheCsprojLogicalName()
    {
        // 这是与 csproj 那条 EmbeddedResource 的契约：改一处不改另一处，取资源会拿到 null 并静默退化
        Assert.Equal("JASM.Elevator.exe", ElevatorProvisioning.EmbeddedResourceName);
        Assert.Equal("Elevator.version", ElevatorProvisioning.VersionMarkerFileName);
    }

    // ── 要不要释放 ───────────────────────────────────────────────

    [Theory]
    [InlineData("3.0.0.0", false, null, false)]   // 同目录那份够用 → 不写盘，folder 版用户不多出一个文件
    [InlineData("3.0.0.0", true, CurrentVersion, false)]
    [InlineData("4.0.0.0", false, null, false)]   // 更高版本同理
    [InlineData("2.0.0.0", false, null, true)]    // 认识刷新但不认识送键 → 不够用
    [InlineData("1.0.0.0", false, null, true)]    // 旧 folder 安装留下的那份（用户实机上就是这个）
    [InlineData("", false, null, true)]           // 版本读不出来：保守按「不够用」处理
    [InlineData("abc", false, null, true)]
    [InlineData(null, false, null, true)]         // 单 exe 用户：压根没有同目录那份
    [InlineData(null, true, CurrentVersion, false)] // 副本在、标记就是当前版本 → 已经是新释放的，不重写
    [InlineData(null, true, "2.27.0", true)]      // 主程序升级过 → 重写（自更新换 exe 后走的就是这一支）
    [InlineData(null, true, "", true)]            // 标记读不到 → 重写
    [InlineData(null, true, null, true)]
    [InlineData("1.0.0.0", true, CurrentVersion, false)] // 副本足够新，同目录那份旧 → 不必再写
    public void ProvisionsOnlyWhenTheSiblingCopyIsUnusableAndTheExtractIsStale(string? siblingVersion,
        bool extractedExists, string? markerVersion, bool expected)
        => Assert.Equal(expected,
            ElevatorProvisioning.ShouldProvision(siblingVersion, extractedExists, markerVersion, CurrentVersion));

    // ── 两个候选取哪一个 ─────────────────────────────────────────

    [Theory]
    [InlineData("sibling.exe", "3.0.0.0", null, null, "sibling.exe", "3.0.0.0")]      // 只有同目录
    [InlineData(null, null, "local.exe", "3.0.0.0", "local.exe", "3.0.0.0")]          // 只有释放副本（单 exe 版）
    [InlineData("sibling.exe", "1.0.0.0", "local.exe", "3.0.0.0", "local.exe", "3.0.0.0")] // 残留陷阱：旧的别盖新的
    [InlineData("sibling.exe", "3.0.0.0", "local.exe", "3.0.0.0", "sibling.exe", "3.0.0.0")] // 平手 → 同目录优先
    [InlineData("sibling.exe", "3.0.0.0", "local.exe", "2.0.0.0", "sibling.exe", "3.0.0.0")] // 同目录更新
    [InlineData("sibling.exe", "9.0.0.0", "local.exe", "10.0.0.0", "local.exe", "10.0.0.0")] // 数值比较而非字符串
    [InlineData("sibling.exe", "abc", "local.exe", "3.0.0.0", "local.exe", "3.0.0.0")] // 同目录版本读不出来时用释放副本
    [InlineData("sibling.exe", "3.0.0.0", "local.exe", null, "sibling.exe", "3.0.0.0")] // 释放副本版本读不出来时不用它
    [InlineData("sibling.exe", "abc", "local.exe", null, "sibling.exe", "abc")]       // 两边都读不出来 → 退回既有行为
    [InlineData(null, null, null, null, null, null)]                                  // 都没有 → 交给「没有可用助手」分支
    public void PrefersTheHigherVersionAndFallsBackToTheSiblingOnTies(string? siblingPath, string? siblingVersion,
        string? extractedPath, string? extractedVersion, string? expectedPath, string? expectedVersion)
    {
        var (path, version) = ElevatorProvisioning.Select(siblingPath, siblingVersion, extractedPath, extractedVersion);

        Assert.Equal(expectedPath, path);
        Assert.Equal(expectedVersion, version);
    }
}