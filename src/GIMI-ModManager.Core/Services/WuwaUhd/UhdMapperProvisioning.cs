namespace GIMI_ModManager.Core.Services.WuwaUhd;

/// <summary>
/// 「要不要把内嵌的 UHD 映射 DLL 释放到磁盘」的决策。与 <c>ElevatorProvisioning</c> 同一套思路：
/// 纯函数、版本一律用字符串收 —— 决策本身能被单测覆盖，落盘动作在 WinUI 的 <c>UhdMapperProvisioner</c>。
///
/// 映射 DLL 只服务于「内置注入启动」（Phase 2），因此释放目标固定为
/// <see cref="UhdActiveModStore.RuntimeDirectory"/> 下的 <see cref="FileName"/>：
/// 提权助手对注入 DLL 的父目录名有白名单（<c>UhdRuntime</c>），不能换位置。
/// </summary>
public static class UhdMapperProvisioning
{
    /// <summary>
    /// 内嵌资源的逻辑名。**必须与 GIMI-ModManager.WinUI.csproj 里那条 EmbeddedResource 的
    /// LogicalName 完全一致**，否则 <c>GetManifestResourceStream</c> 拿到 null，
    /// 单 exe 版会静默退化成「没有映射 DLL」。
    /// </summary>
    public const string EmbeddedResourceName = "JASM.UhdMapper.dll";

    /// <summary>磁盘上的文件名。提权助手按这个名字与父目录名 <c>UhdRuntime</c> 校验。</summary>
    public const string FileName = "Jasm.UhdMapper.dll";

    /// <summary>
    /// 释放目录里记录「这份副本是哪个主程序版本释放的」的标记文件。
    /// 与助手同一取舍：绑主程序版本，自更新后标记失配、下次启动重写。
    /// </summary>
    public const string VersionMarkerFileName = "UhdMapper.version";

    /// <summary>
    /// 已释放的副本要不要重写。文件不在、标记读不到/为空、标记与当前主程序版本不等，任一成立就要写。
    /// 保守方向是**多写一次**，而不是「拿着一个来路不明的副本当真」。
    /// </summary>
    public static bool ShouldProvision(bool extractedExists, string? markerVersion, string currentVersion)
    {
        if (!extractedExists)
            return true;

        if (string.IsNullOrWhiteSpace(markerVersion))
            return true;

        return !string.Equals(markerVersion, currentVersion, StringComparison.Ordinal);
    }
}