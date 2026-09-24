using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace GIMI_ModManager.WinUI.Services.Input;

/// <summary>
/// 合成输入的**唯一**一份实现 —— 主程序（<see cref="GameKeySender"/>）和提权助手（<c>KeyHelperHost</c>）
/// 都调这里。两份实现会让「主程序发出去的键」和「助手发出去的键」慢慢长出差异，而这类差异在真机上
/// 只表现为「mod 偶尔没反应」，极难排查。
///
/// 语义（改之前先读）：整个和弦**一次性**提交给 <c>SendInput</c>；
/// 按下顺序是「先修饰键后主键」，抬起顺序**反过来** ——
/// 否则游戏可能读到「主键还按着但修饰键已经松开」，把 Alt+↑ 认成单个 ↑。
///
/// 除了按键，这里也放**不产生任何按键语义**的那一次注入（<see cref="SendNeutralMouseMove"/>）：
/// 它同样是一次 <c>SendInput</c>，同样只该有一份实现。
/// </summary>
internal static unsafe class KeyChordSender
{
    /// <summary>按下到抬起之间的保持时长。固定短按，不做「点一下按住、再点一下松开」。</summary>
    internal const int HoldMilliseconds = 80;

    /// <summary>
    /// 把整个和弦一次性提交，返回 <c>SendInput</c> 真正插入的事件数（= 请求数才算成功）。
    /// </summary>
    internal static uint SendChord(ushort virtualKey, IReadOnlyList<ushort> modifierKeyCodes, bool keyUp)
    {
        var count = modifierKeyCodes.Count + 1;
        var inputs = stackalloc INPUT[count];
        var index = 0;

        if (keyUp)
        {
            inputs[index++] = CreateKeyboardInput(virtualKey, true);
            foreach (var modifier in modifierKeyCodes)
                inputs[index++] = CreateKeyboardInput(modifier, true);
        }
        else
        {
            foreach (var modifier in modifierKeyCodes)
                inputs[index++] = CreateKeyboardInput(modifier, false);

            inputs[index] = CreateKeyboardInput(virtualKey, false);
        }

        return PInvoke.SendInput((uint)count, inputs, sizeof(INPUT));
    }

    /// <summary>
    /// 注入一次「零位移鼠标移动」：不改光标位置、不产生任何按键。它唯一的作用是把
    /// 「最近收到输入的那个进程」这个身份拿回本进程。
    ///
    /// <b>为什么需要它</b>：Windows 的前台锁只认「自己就是前台进程 / 最近收到过输入的那个进程」，
    /// 而**注入的输入算在注入者头上**。2026-09-24 本机实测（探针脚本，两个进程一抢一测）：
    /// 甲进程注入一次点击后，乙进程连调 <c>SetForegroundWindow</c> 一律被拒；甲进程一退出，
    /// 乙的下一次调用立刻成功。所以浮窗被抢走前台之后想拿回来，必须先自己注入一次输入 ——
    /// 光反复调 <c>SetForegroundWindow</c> 只会一秒一条「被拒」，直到别人退出为止。
    ///
    /// <b>为什么用鼠标而不是 Alt 点按</b>（后者是流传很广的解锁写法）：那一下会被前台窗口当成真的
    /// Alt —— 弹出它的菜单栏、触发游戏自己的 Alt 绑定；更要命的是万一与送键路径撞拍，就变成
    /// Alt+F10，正是本仓库明令不许触发的那件事。零位移鼠标事件的副作用只有"收方多收到一个 0 位移
    /// 的移动事件"，对游戏（按帧算增量）等于什么都没发生。
    ///
    /// 返回 <c>SendInput</c> 插入的事件数（1 = 成功）。插入失败不影响调用方的后续动作：
    /// 大不了这次 <c>SetForegroundWindow</c> 被拒，下一拍再试。
    /// </summary>
    internal static unsafe uint SendNeutralMouseMove()
    {
        var inputs = stackalloc INPUT[1];
        inputs[0] = new INPUT
        {
            type = INPUT_TYPE.INPUT_MOUSE,
            Anonymous = new INPUT._Anonymous_e__Union
            {
                // MOUSEEVENTF_MOVE 且 dx/dy 全 0 = 一次原地移动。**不要**加 MOUSEEVENTF_ABSOLUTE：
                // 那要求 dx/dy 是 0..65535 的归一化坐标，会把光标拽到屏幕角落。
                mi = new MOUSEINPUT
                {
                    dx = 0,
                    dy = 0,
                    mouseData = 0,
                    dwFlags = MOUSE_EVENT_FLAGS.MOUSEEVENTF_MOVE,
                    time = 0,
                    dwExtraInfo = 0
                }
            }
        };

        return PInvoke.SendInput(1, inputs, sizeof(INPUT));
    }

    private static INPUT CreateKeyboardInput(ushort virtualKey, bool keyUp) => new()
    {
        type = INPUT_TYPE.INPUT_KEYBOARD,
        Anonymous = new INPUT._Anonymous_e__Union
        {
            ki = new KEYBDINPUT
            {
                wVk = (VIRTUAL_KEY)virtualKey,
                wScan = 0,
                dwFlags = keyUp ? KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP : 0,
                time = 0,
                dwExtraInfo = 0
            }
        }
    };
}