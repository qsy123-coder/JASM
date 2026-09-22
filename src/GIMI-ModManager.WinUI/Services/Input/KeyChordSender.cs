using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace GIMI_ModManager.WinUI.Services.Input;

/// <summary>
/// 合成按键的**唯一**一份实现 —— 主程序（<see cref="GameKeySender"/>）和提权助手（<c>KeyHelperHost</c>）
/// 都调这里。两份实现会让「主程序发出去的键」和「助手发出去的键」慢慢长出差异，而这类差异在真机上
/// 只表现为「mod 偶尔没反应」，极难排查。
///
/// 语义（改之前先读）：整个和弦**一次性**提交给 <c>SendInput</c>；
/// 按下顺序是「先修饰键后主键」，抬起顺序**反过来** ——
/// 否则游戏可能读到「主键还按着但修饰键已经松开」，把 Alt+↑ 认成单个 ↑。
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
