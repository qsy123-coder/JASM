namespace GIMI_ModManager.Core.Services.WuwaModFixer;

/// <summary>单个 Mod 的修复结果。</summary>
public enum WuwaFixOutcome
{
    Success,
    Skipped,
    Failed,
    Cancelled
}

/// <summary>结果 + 给人看的一句话原因。</summary>
public sealed record WuwaFixClassification(WuwaFixOutcome Outcome, string Message);

/// <summary>
/// 把「退出码 / NDJSON finish / 备份有没有变化 / 是否取消」翻译成一个结果。
///
/// 成功判定刻意走**退出码 + finish.success**，而不是「跑完就算成功」：
/// 上游在部分情况下会以 0 退出但 finish.success=false，那种 Mod 不能计入成功。
/// 备份有变化但没收到 finish（旧版 CLI/输出被截断）时也认成功 —— 文件确实被改了，
/// 让用户误以为没修反而更糟。
/// </summary>
public static class WuwaFixOutcomeClassifier
{
    public static WuwaFixClassification Classify(
        int exitCode,
        WuwaModCliEvent? finish,
        bool backupChanged,
        bool timedOut,
        bool cancelled)
    {
        if (cancelled)
            return new WuwaFixClassification(WuwaFixOutcome.Cancelled, "已终止");

        if (timedOut)
            return new WuwaFixClassification(WuwaFixOutcome.Failed, "执行超时");

        var finishMessage = finish is { Kind: WuwaModCliEventKind.Finish } ? finish.Message : null;

        if (finish is { Kind: WuwaModCliEventKind.Finish, Success: false })
            return new WuwaFixClassification(WuwaFixOutcome.Failed, finishMessage ?? "修复失败");

        if (exitCode == 0 && !backupChanged)
        {
            // 上游对「没有可修的内容」也会回 success=true（实测：未匹配角色 → 0 个文件被处理）。
            // 只要没有产生任何 .BAK，就按「跳过」记账，别把空跑算成修好了。
            return new WuwaFixClassification(WuwaFixOutcome.Skipped,
                finishMessage ?? "未发现需要修复的内容");
        }

        if (finish is { Kind: WuwaModCliEventKind.Finish, Success: true })
            return new WuwaFixClassification(WuwaFixOutcome.Success, finishMessage ?? "修复完成");

        if (exitCode == 0)
            return new WuwaFixClassification(WuwaFixOutcome.Success, "修复完成（未收到完成事件）");

        return new WuwaFixClassification(WuwaFixOutcome.Failed,
            string.IsNullOrWhiteSpace(finishMessage) ? $"退出码 {exitCode}" : finishMessage);
    }
}