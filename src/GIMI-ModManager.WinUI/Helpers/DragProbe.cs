using System.Diagnostics;
using Microsoft.UI.Xaml;
using Serilog;

namespace GIMI_ModManager.WinUI.Helpers;

/// <summary>
/// 拖拽探针：把「拖拽事件到底有没有到这一层」记进日志。
///
/// <para>
/// <b>为什么需要它</b>：提权会让跨完整性级别的拖拽在 UIPI 那层被整个掐掉 —— 事件根本不进进程，
/// 于是所有落点都不会说话，日志里一片空白，排查只能靠问用户「光标是禁止还是复制」。
/// 探针分两层挂（窗口根 + 各个页面）就能一眼看出事件停在哪：
/// <b>窗口根也没有</b> ⇒ 挡在 JASM 之外（权限/第三方钩子）；<b>根有、页面没有</b> ⇒ 是层级/覆盖问题。
/// </para>
///
/// <para>
/// <b>节流按时间（3 秒）而不是「一轮拖拽只记一次」</b>：DragOver 每动一下就一发，不节流会把
/// 日志刷爆；而按「一轮一次」记账在父子元素之间来回冒的事件上并不可靠（与四处落点拒绝提示同一套理由）。
/// </para>
/// </summary>
internal static class DragProbe
{
    private const int ThrottleMs = 3000;

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static long _lastLogMs = -ThrottleMs;

    /// <summary>记一笔拖拽事件。绝不抛异常 —— 诊断代码不能反过来把拖拽搞坏。</summary>
    internal static void Log(string where, DragEventArgs e)
    {
        try
        {
            var now = Clock.ElapsedMilliseconds;
            if (now - Interlocked.Read(ref _lastLogMs) < ThrottleMs)
                return;

            Interlocked.Exchange(ref _lastLogMs, now);

            // 带上 formats：源没给 FileDrop（例如从某些窗口里拖出的裸文本）时，
            // 「事件到了但没有文件」和「事件根本没到」是两件事，日志必须能分开。
            // 写全名 Serilog.Log：本类自己也有个 Log 方法，不限定就会解析到自己头上（CS0119）。
            Serilog.Log.Information("[拖拽探针] {Where} 收到拖拽：formats=[{Formats}]", where,
                string.Join(",", e.DataView.AvailableFormats));
        }
        catch (Exception)
        {
            // 有意吞掉
        }
    }
}
