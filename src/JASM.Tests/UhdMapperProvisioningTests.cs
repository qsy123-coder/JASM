using System.IO;
using GIMI_ModManager.Core.Helpers;
using GIMI_ModManager.Core.Services.WuwaUhd;

namespace JASM.Tests;

/// <summary>
/// 覆盖 <see cref="UhdMapperProvisioning"/> 与它和提权助手协议之间的契约：
/// 文件名/目录名对不上时，助手的路径白名单会直接拒绝注入。
/// </summary>
public class UhdMapperProvisioningTests
{
    [Fact]
    public void EmbeddedResourceNameMatchesTheCsprojLogicalName()
        => Assert.Equal("JASM.UhdMapper.dll", UhdMapperProvisioning.EmbeddedResourceName);

    [Fact]
    public void FileNameMatchesTheElevatorWhitelist()
    {
        Assert.Equal(ElevatorLaunchInjectProtocol.MapperDllFileName, UhdMapperProvisioning.FileName);
        Assert.Equal(ElevatorLaunchInjectProtocol.MapperDirectoryName,
            Path.GetFileName(UhdActiveModStore.RuntimeDirectory));
    }

    [Theory]
    [InlineData(false, null, "1.2.3", true)] // 还没释放过
    [InlineData(true, "1.2.3", "1.2.3", false)] // 同版本，不重写
    [InlineData(true, "1.2.2", "1.2.3", true)] // 主程序升级过
    [InlineData(true, null, "1.2.3", true)] // 标记丢了，保守重写
    [InlineData(true, "  ", "1.2.3", true)] // 标记为空
    public void ShouldProvisionRewritesUnlessTheMarkerMatches(bool extractedExists, string? markerVersion,
        string currentVersion, bool expected)
        => Assert.Equal(expected,
            UhdMapperProvisioning.ShouldProvision(extractedExists, markerVersion, currentVersion));
}