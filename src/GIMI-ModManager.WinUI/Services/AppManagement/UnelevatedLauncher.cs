using System.Diagnostics;
using GIMI_ModManager.WinUI.Services.Input;
using Serilog;

namespace GIMI_ModManager.WinUI.Services.AppManagement;

/// <summary>
/// 「以普通权限启动一个进程」—— 也就是**去提权**。
///
/// <para>
/// 为什么要它：JASM 被提权运行之后，跨完整性级别的拖拽会被 UIPI 整个掐掉（Explorer 是中完整性、
/// 提权后的 JASM 是高），而用户往往不知道自己是怎么被提权的 —— exe 属性 → 兼容性里勾一下
/// 「以管理员身份运行此程序」，之后双击就是提权运行。有了它就能一键回到普通权限，
/// 不用让用户去翻那个勾。
/// </para>
///
/// <para>
/// 做法是**请 explorer.exe 代起这个进程**：explorer 自己是中完整性，它启动的子进程也是中完整性。
/// 这是 Windows 上「从提权的进程里起一个不提权的进程」的通用的土办法 —— 系统没有提供
/// 「去提权」的 API，`ShellExecute` 的 <c>runas</c> 只能往上走，往下走只有借 shell 的令牌这一条路；
/// 而借令牌那条路（<c>DuplicateTokenEx</c> + <c>CreateProcessWithTokenW</c>）在本项目的
/// CsWin32 里生成不出来（同批请求的其他函数都能生成，只有它不吐），所以这里走 explorer。
/// </para>
///
/// <para>
/// <b>代价是有意接受的</b>：<c>explorer.exe "路径"</c> **传不了参数**、也控制不了子进程的工作目录。
/// 参数这一件事由<b>交接标记文件</b>补上（见 <c>UnelevatedRelaunchProtocol</c>）；
/// 工作目录不需要管 —— 日志走的是绝对路径（<c>App.xaml.cs</c> 里 <c>AppContext.BaseDirectory</c> 拼的）。
/// </para>
/// </summary>
internal static class UnelevatedLauncher
{
    /// <summary>
    /// 启动结果。<paramref name="ProcessId"/> 恒为 0：经 explorer 起的那一份拿不到 pid
    /// （explorer 只负责转交，不等它、也不回报），所以调用方别指望用它做后续判定。
    /// </summary>
    internal readonly record struct LaunchResult(bool Success, string Detail);

    /// <summary>以普通权限启动 <paramref name="exePath"/>。</summary>
    internal static LaunchResult Launch(string exePath, ILogger logger)
    {
        try
        {
            // UseShellExecute = false：走 CreateProcess，不经过 shell 的动词解析 ——
            // 我们要的是**用当前（提权的）身份去起 explorer**，让 explorer 再往下起中完整性的子进程。
            // 打开 UseShellExecute 反而会让 Windows 按目标 exe 自己的清单决定提不提权，方向就反了。
            using var explorer = Process.Start(new ProcessStartInfo("explorer.exe", $"\"{exePath}\"")
            {
                UseShellExecute = false
            });

            if (explorer is null)
            {
                logger.Error("去提权重启：explorer.exe 没能启动");
                return new LaunchResult(false, "explorer.exe 没能启动");
            }

            logger.Information("去提权重启：已经请 explorer.exe 代起 {ExePath}", exePath);
            return new LaunchResult(true, "经 explorer.exe 启动（新进程为普通权限）");
        }
        catch (Exception e)
        {
            logger.Error(e, "去提权重启：请 explorer.exe 代起失败");
            return new LaunchResult(false, e.Message);
        }
    }

    /// <summary>
    /// 读某个进程现在的完整性级别，只用来**验**这件事（日志里说真话）：
    /// 去提权成功的话，新的一份应该是「中」而不是「高」。
    /// 读不到返回 <c>null</c>，调用方按「不知道」处理。
    /// </summary>
    internal static string DescribeIntegrity(uint processId) =>
        WindowProcessQuery.TryReadIntegrityLevelRid(processId) switch
        {
            null => "<读不到>",
            0x1000 => "低",
            0x2000 => "中（普通权限）",
            0x3000 => "高（管理员）",
            var rid => $"0x{rid:X}"
        };
}