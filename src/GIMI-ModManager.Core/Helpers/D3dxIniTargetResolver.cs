namespace GIMI_ModManager.Core.Helpers;

/// <summary>
/// 「d3dx.ini 在哪」+「里面写的游戏进程名是什么」。
///
/// 这两段原本是 <c>GameKeySender</c> 的 private 方法，因为提权刷新链路（<c>ElevatorService</c>）
/// 要用同一份判断，所以挪进 Core —— 放这里既只有一份实现，也能被单测覆盖
/// （WinUI 与 Elevator 两个工程都不在测试项目的引用范围内，挪出去就没法自动回归）。
///
/// 候选路径的判断**一字不能改**：<c>GimiRootFolderPath</c> 可能是空串
/// （<c>SkinManagerService.ThreeMigotoRootfolder</c> 取不到目录时就是 <c>""</c>），
/// 此时 <c>Path.Combine("", "d3dx.ini")</c> 会生成相对路径、在调用时才落到进程的当前目录上，
/// 于是「有没有这个文件」会随 CWD 变化 —— 所以空 / 空白路径必须不产生候选。
/// </summary>
public static class D3dxIniTargetResolver
{
    /// <summary>XXMI / 3DMigoto 的配置文件，与 <c>Mods</c> 同级。</summary>
    public const string D3dxIniFileName = "d3dx.ini";

    /// <summary>
    /// 候选路径，按可信度排序：GIMI 根目录正下方 → Mods 的父目录。
    /// 不跳 <c>ModManagerOptions.XxmiRootFolderPath</c>：它不是 d3dx.ini 所在处。
    /// </summary>
    public static IReadOnlyList<string> GetD3dxIniCandidates(string? gimiRootFolderPath, string? modsFolderPath)
    {
        var candidates = new List<string>(2);

        if (!string.IsNullOrWhiteSpace(gimiRootFolderPath))
            candidates.Add(Path.Combine(gimiRootFolderPath, D3dxIniFileName));

        var trimmedModsFolderPath = modsFolderPath?.TrimEnd('\\', '/');
        var modsRootFolder = string.IsNullOrWhiteSpace(trimmedModsFolderPath)
            ? null
            : Path.GetDirectoryName(trimmedModsFolderPath);

        if (!string.IsNullOrWhiteSpace(modsRootFolder))
            candidates.Add(Path.Combine(modsRootFolder, D3dxIniFileName));

        return candidates;
    }

    /// <summary>候选里第一个真实存在的 d3dx.ini；一个都没有则返回 <c>null</c>。</summary>
    public static string? ResolveD3dxIniPath(string? gimiRootFolderPath, string? modsFolderPath)
    {
        foreach (var candidate in GetD3dxIniCandidates(gimiRootFolderPath, modsFolderPath))
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>
    /// 读 ini 并解析出目标进程名（不含 <c>.exe</c>）。
    /// 读不了不算异常：游戏运行中 d3dx.ini 可能被独占打开，调用方按「这次没解析出目标」处理即可。
    /// </summary>
    public static string? ReadTargetProcessName(string iniPath)
    {
        try
        {
            return D3dxIniTargetParser.ParseTargetProcessName(File.ReadAllText(iniPath));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}