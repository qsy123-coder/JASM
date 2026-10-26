namespace GIMI_ModManager.Core.Services.WuwaModFixer;

/// <summary>
/// 一个待修复的 Mod。路径在开始修复时快照下来，运行中角色列表被刷新也不受影响。
/// </summary>
public sealed record WuwaFixTarget(Guid Id, string Name, string ModPath);

/// <summary>失败/跳过的单个 Mod 及原因。</summary>
public sealed record WuwaFixFailure(string ModName, string Reason, bool IsSkipped = false);

/// <summary>一次修复运行的汇总。</summary>
public sealed record WuwaFixRunSummary(
    int Total,
    int Success,
    int Skipped,
    int Failed,
    bool WasCancelled,
    IReadOnlyList<WuwaFixFailure> Failures)
{
    /// <summary>有文件被改过，才需要发 F10。</summary>
    public bool HasChanges => Success > 0;

    /// <summary>给浮窗状态行用的一句话摘要（不含本地化，界面自行拼）。</summary>
    public string ToSummaryText() =>
        WasCancelled
            ? $"已终止：成功 {Success} / 跳过 {Skipped} / 失败 {Failed}"
            : $"完成：成功 {Success} / 跳过 {Skipped} / 失败 {Failed}";
}