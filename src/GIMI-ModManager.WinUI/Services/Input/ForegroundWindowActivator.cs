using System.Runtime.InteropServices;
using Serilog;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace GIMI_ModManager.WinUI.Services.Input;

/// <summary>
/// 把目标游戏窗口还原并切到前台 —— 送键（<c>GameKeySender</c>）与提权刷新（<c>ElevatorService</c>）共用。
///
/// <b>为什么必须由调用方在同步段里做，不能交给提权助手</b>：Windows 的前台锁只认
/// 「自己就是前台进程 / 最近收到输入的那个进程」，**与完整性级别无关**。助手是个后台进程，
/// 它调 <c>SetForegroundWindow</c> 会被系统静默拒掉（只在任务栏闪一下），紧接着它自己的前台回读
/// 校验必然失败、回执 <c>not-foreground</c> —— 用户看到的就是「提权助手没能把游戏切到前台」。
///
/// 现场签名能认出这件事：**JASM 重启后第一次发键成功，之后每一次都失败**。助手只在刚被 UAC
/// 拉起的那一次抢得动 —— 那一刻用户刚点过 UAC，等于刚给过输入，它才短暂地拿到前台身份。
///
/// 所以切前台这一步跟着「用户刚点过的那个进程」走：谁调 <see cref="Activate"/>，谁就必须
/// **还没 await 过**（一旦让出，前台身份随时可能易主）。提权那一档额外把这次的权利让出去
/// （<see cref="PInvoke.AllowSetForegroundWindow"/>），助手随后那次调用才算数。
///
/// 两个调用方都只把这里当作「尽力而为」：切没切过去由**之后的回读校验**判定
/// （助手那边会再校验一次），这里不抛异常、也不等待。
/// </summary>
internal static class ForegroundWindowActivator
{
    /// <summary>
    /// <c>AllowSetForegroundWindow</c> 的 <c>ASFW_ANY</c>：把「这一次设置前台窗口」的权利授予**所有**进程。
    ///
    /// 用 ASFW_ANY 而不是提权助手的 pid：按 pid 授权要求调用方能以 <c>PROCESS_SET_INFORMATION</c>
    /// 打开那个进程，而本进程是「中」完整性、助手是「高」，这一步会直接被拒。
    /// </summary>
    private const uint AsfwAny = 0xFFFFFFFF;

    /// <summary>
    /// 还原（若最小化）→ 切前台 → 视需要把这次的权利让出去。
    /// <paramref name="handOverRightToSetForeground"/> 只在**要请别人（提权助手）也试着切**时为 true；
    /// 自己发键、不需要助手的场景别设，免得白白把系统级权利发给所有进程。
    /// <paramref name="nudgeInputForForegroundLock"/> 见下面的说明 —— **只有浮窗那条"前台被抢走后再拿回来"的路该设 true**。
    /// </summary>
    /// <remarks>
    /// <b>什么时候才设 <paramref name="nudgeInputForForegroundLock"/></b>：本进程刚"收到过输入"时（用户按了热键、
    /// 点了我们自己的窗口）它就是输入所有者，直接调 <c>SetForegroundWindow</c> 系统就放行 —— 那时不需要注入任何东西。
    /// 但如果前台是**被别人**拿走的（游戏自己抢回去 / 用户点了别的窗口），本进程的输入身份已经过期，
    /// 上面那句会被前台锁拒掉，而它**不会**因为再调几次就放行（实测连撞 16 拍全被拒，直到抢走它的那个进程退出）。
    /// 唯一的解是把身份拿回来：先注入一次输入（<see cref="KeyChordSender.SendNeutralMouseMove"/>，
    /// 一次零位移鼠标移动，无按键语义），紧接着的那句 <c>SetForegroundWindow</c> 就通过了 —— 同一组实测里
    /// 这一步是"立刻成功"。
    ///
    /// <b>送键路径（提权助手那一档在内）绝不能设 true</b>：它的下一句就是 F10；在它前面注入哪怕一次键类事件，
    /// 都可能把那次刷新变成组合键（Alt+F10 会触发英伟达 App 的录屏，本仓库明令不许）。
    /// 现在用的是鼠标事件，撞拍的风险已经不存在 —— 但这条界线照旧：送键路径只做"切前台"这一件事。
    /// </remarks>
    internal static void Activate(HWND window, bool handOverRightToSetForeground, ILogger logger,
        bool nudgeInputForForegroundLock = false)
    {
        if (window.IsNull)
            return;

        // 最小化的窗口也能被 SetForegroundWindow 选中，但那之后仍然是「最小化」状态，按键打不进窗口
        if (PInvoke.IsIconic(window) != 0)
            PInvoke.ShowWindow(window, SHOW_WINDOW_CMD.SW_RESTORE);

        // 必须在 SetForegroundWindow **之前**：前台锁是在调用那一刻评判"谁最近收到过输入"的
        if (nudgeInputForForegroundLock && KeyChordSender.SendNeutralMouseMove() == 0)
        {
            logger.Warning("[ForegroundWindowActivator] 注入空鼠标事件失败，"
                           + "这次 SetForegroundWindow 多半会被前台锁拒绝");
        }

        // 返回值不可信（前台锁会让它返回 0，而窗口其实已经切过去了），所以只记日志、不当失败处理，
        // 真正的判定交给之后的回读校验。Win32 错误码留给排查：被拒时它通常是 ERROR_ACCESS_DENIED。
        if (PInvoke.SetForegroundWindow(window) == 0)
        {
            logger.Warning("[ForegroundWindowActivator] SetForegroundWindow 被拒（Win32 错误 {Error}），"
                           + "仍按当前前台窗口继续", Marshal.GetLastWin32Error());
        }

        if (!handOverRightToSetForeground)
            return;

        // 上面那次调用没生效的话，助手随后还有一次机会。放在最后是有意的 —— 这份授权是给
        // **下一次**别的进程调 SetForegroundWindow 用的，越贴近助手真正动手的时刻越新鲜。
        if (PInvoke.AllowSetForegroundWindow(AsfwAny) == 0)
        {
            logger.Warning("[ForegroundWindowActivator] AllowSetForegroundWindow 失败（Win32 错误 {Error}），"
                           + "助手将无法在 SetForegroundWindow 被拒后自行重试", Marshal.GetLastWin32Error());
        }
    }
}