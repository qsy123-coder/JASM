using System.Text;

namespace JASM.OverlaySpike;

/// <summary>
/// 原型的最小落盘日志。
///
/// **为什么补这个**：原型第一版刻意不写日志，理由是"诊断信息全在面板上实时显示，不落盘更方便"。
/// 这个判断是错的 —— 用户报告"点了一下浮窗它就自己消失了，热键也唤不回来"，
/// 而窗口一旦消失、进程一旦退出，屏幕上那块面板就什么也看不到了，等于**在最需要证据的时候把证据弄丢了**。
/// 所以诊断工具的第一原则是先把东西写进文件，再考虑怎么显示。
///
/// 不引 Serilog（虽然主工程用）：原型多引一个依赖就多一个构建失败的理由，
/// 这里 <see cref="File.AppendAllText"/> 足够。
/// </summary>
internal static class SpikeLog
{
    private static readonly object Gate = new();

    /// <summary>日志文件绝对路径。放在 exe 旁边（`bin/.../spike.log`），方便直接打开。</summary>
    internal static string FilePath { get; } = Path.Combine(AppContext.BaseDirectory, "spike.log");

    /// <summary>写一行带时间戳的日志。任何写入失败都被吞掉 —— 记日志失败绝不能反过来把原型搞崩。</summary>
    internal static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                var line = new StringBuilder()
                    .Append(DateTime.Now.ToString("HH:mm:ss.fff"))
                    .Append("  ")
                    .Append(message)
                    .Append(Environment.NewLine)
                    .ToString();

                File.AppendAllText(FilePath, line, Encoding.UTF8);
            }
        }
        catch
        {
            // 故意吞掉：诊断工具不该因为诊断本身失败而影响被测对象
        }
    }

    /// <summary>清空日志并写一个新的会话头。每次启动都从干净的文件开始，避免翻上一次的记录。</summary>
    internal static void StartSession()
    {
        try
        {
            File.WriteAllText(FilePath, string.Empty, Encoding.UTF8);
        }
        catch
        {
            // 同上
        }

        Write($"=== OverlaySpike 启动  pid={Environment.ProcessId} ===");
        Write($"=== 是否已提权(管理员): {IsElevated()} ===");
        Write($"=== 日志文件: {FilePath} ===");
    }

    /// <summary>
    /// 本进程是否以管理员运行。这一条对 Phase 0 很关键：
    /// 要验的就是"非提权进程的浮窗能不能盖在提权的鸣潮上"，
    /// 如果日志显示这里为 True，那整个测试的前提就错了、结论不可用。
    /// </summary>
    private static bool IsElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}