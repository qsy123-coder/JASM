namespace GIMI_ModManager.Core.Helpers;

/// <summary>
/// 「要不要把内嵌的提权助手释放到磁盘 / 两个候选用哪一个」的决策。
///
/// 单 exe 版（用户手上的分发形态）没有随包的 <c>Elevator.exe</c>，助手只能从主 exe 的内嵌资源里释放出来
/// （执行体在 WinUI 的 <c>ElevatorProvisioner</c>）；folder 版本来就在 exe 同目录有一份。
/// 于是运行时永远面对两个候选，本类就是选它的规则。
///
/// 全是纯函数、版本一律用字符串收（不收 <c>FileVersionInfo</c>：它没有公开构造函数，收了就没法单测）——
/// 与 <see cref="ElevatorKeySendProtocol"/> 同一取舍，逻辑才能被 JASM.Tests 覆盖。
/// </summary>
public static class ElevatorProvisioning
{
    /// <summary>
    /// 内嵌资源的逻辑名。**必须与 GIMI-ModManager.WinUI.csproj 里那条 EmbeddedResource 的 LogicalName 完全一致**，
    /// 否则 <c>GetManifestResourceStream</c> 拿到 null，单 exe 版会静默退化成「没有助手」。
    /// </summary>
    public const string EmbeddedResourceName = "JASM.Elevator.exe";

    /// <summary>
    /// 释放目录里记录「这份副本是哪个主程序版本释放的」的标记文件。
    /// 绑的是**主程序**版本而不是助手版本：自更新换掉主 exe 之后版本必然变化，标记随之失配，
    /// 下次启动就会重写助手 —— 助手版本的推进不需要另立一套机制。
    /// </summary>
    public const string VersionMarkerFileName = "Elevator.version";

    /// <summary>
    /// 要不要把内嵌的助手写盘。<paramref name="siblingVersion"/> 是同目录那份的 FileVersion
    /// （<c>null</c> = 没有这个文件，或版本读不出来）。
    /// </summary>
    /// <remarks>
    /// 同目录那份**已经够用就不写盘**：folder 版用户不该因为我们多了一个内嵌副本就平白多出一个文件，
    /// 也省掉一次「往磁盘落 exe」的动作（那是杀软会盯的行为）。
    /// 「够用」的判据取 <see cref="ElevatorKeySendProtocol.SupportsKeySend"/>（下限 3.0.0.0）而不是刷新那条：
    /// 版本标记只增不改，能满足送键的助手天然也满足带目标窗口的刷新（下限 2.0.0.0），
    /// 所以「最高要求」就是「够不够用」的等价判据，不用把两个谓词都问一遍。
    /// </remarks>
    public static bool ShouldProvision(string? siblingVersion, bool extractedExists, string? markerVersion,
        string currentVersion)
    {
        if (ElevatorKeySendProtocol.SupportsKeySend(siblingVersion))
            return false;

        return NeedsExtraction(extractedExists, markerVersion, currentVersion);
    }

    /// <summary>
    /// 两个候选里挑一个，返回它的 (路径, FileVersion)。按版本取高者，**平手或都读不出来时同目录那份优先**。
    /// 两边都没有时返回 <c>(null, null)</c> —— 调用方据此进「没有可用助手」的既有分支。
    /// </summary>
    /// <remarks>
    /// 不能简单地「同目录优先」：单 exe 用户的目录里可能躺着旧 folder 安装留下的 <c>1.0.0.0</c> 助手
    /// （用户实机上就有），那样会把刚从内嵌资源写出来的、能用的新助手盖掉 —— 正好是这次要根治的错配。
    /// 但平手时反过来偏向同目录：那份是随包安装的，位置更「正式」，而释放副本每换个主程序版本就会重写一次。
    /// </remarks>
    public static (string? Path, string? Version) Select(string? siblingPath, string? siblingVersion,
        string? extractedPath, string? extractedVersion)
    {
        if (siblingPath is null)
            return (extractedPath, extractedVersion);

        if (extractedPath is null)
            return (siblingPath, siblingVersion);

        return PreferExtractedCopy(siblingVersion, extractedVersion)
            ? (extractedPath, extractedVersion)
            : (siblingPath, siblingVersion);
    }

    /// <summary>
    /// 已释放的副本要不要重写。文件不在、标记读不到/为空、标记与当前主程序版本不等，任一成立就要写。
    /// 保守方向是**多写一次**，而不是「拿着一个来路不明的副本当真」。
    /// </summary>
    private static bool NeedsExtraction(bool extractedExists, string? markerVersion, string currentVersion)
    {
        if (!extractedExists)
            return true;

        if (string.IsNullOrWhiteSpace(markerVersion))
            return true;

        return !string.Equals(markerVersion, currentVersion, StringComparison.Ordinal);
    }

    /// <summary>
    /// 释放副本是否胜过同目录那份。释放副本的版本读不出来就**不用它**（不确定的东西不当真）；
    /// 同目录那份的版本读不出来时反过来**用释放副本** —— 那是我们刚从自己的资源里写出来的，确定可用。
    /// </summary>
    private static bool PreferExtractedCopy(string? siblingVersion, string? extractedVersion)
    {
        if (!Version.TryParse(extractedVersion, out var extracted))
            return false;

        if (!Version.TryParse(siblingVersion, out var sibling))
            return true;

        return extracted > sibling;
    }
}