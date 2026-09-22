namespace GIMI_ModManager.Core.Helpers;

/// <summary>
/// 按键组合的危险护栏 —— 主程序（<c>GameKeySender</c>）与提权助手（<c>KeyHelperHost</c>）共用这一份。
///
/// **为什么必须共用**：助手是另一个进程，护栏写两份就会出现「主程序拦得住、助手放过去了」这种
/// 最糟糕的偏差（助手那侧提权，放过去的后果更严重）。所以规则只有这一份实现。
///
/// 规则（从 <c>GameKeySender</c> 原样搬来，语义逐字未改）：
/// <list type="bullet">
/// <item>没按 Alt 时，单发 Win 键要拦 —— 会把开始菜单 / 游戏栏叫出来。</item>
/// <item>按着 Alt 时，Alt+F4 关游戏，Alt+Tab / Alt+Esc 把焦点抢走。</item>
/// </list>
/// 注意 <c>Alt+Win</c> 目前**不拦**（既有行为如此，不在这里顺手改）。
/// </summary>
public static class KeyChordGuard
{
    // 危险组合键用到的键码（VK 常量不会被 CsWin32 生成成完备枚举，按需声明）
    public const ushort VkShift = 0x10;
    public const ushort VkCtrl = 0x11;
    public const ushort VkMenu = 0x12; // Alt
    private const ushort VkLWin = 0x5B;
    private const ushort VkRWin = 0x5C;
    private const ushort VkF4 = 0x73;
    private const ushort VkTab = 0x09;
    private const ushort VkEscape = 0x1B;

    /// <summary>修饰键白名单：只认 Ctrl / Shift / Alt（字母键、鼠标键都不是合法修饰键）。</summary>
    public static bool IsAllowedModifier(ushort virtualKey)
        => virtualKey is VkCtrl or VkShift or VkMenu;

    /// <summary>
    /// 修饰键整组校验：全部在白名单内、不得重复、最多 3 个。
    /// 重复的修饰键会让「按下 N 个 / 抬起 N 个」语义变糊，直接拒掉。
    /// </summary>
    public static bool IsAllowedModifiers(IReadOnlyList<ushort> modifierKeyCodes)
    {
        if (modifierKeyCodes.Count > 3)
            return false;

        for (var i = 0; i < modifierKeyCodes.Count; i++)
        {
            if (!IsAllowedModifier(modifierKeyCodes[i]))
                return false;

            for (var j = i + 1; j < modifierKeyCodes.Count; j++)
            {
                if (modifierKeyCodes[i] == modifierKeyCodes[j])
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 危险组合键护栏：写了 <c>key = alt F4</c> 的 mod 照发会把用户的游戏直接关掉。
    /// </summary>
    public static bool IsBlockedChord(ushort virtualKey, IReadOnlyList<ushort> modifierKeyCodes)
    {
        // 没按 Alt 时：单发 Win 键也要拦（会把开始菜单 / 游戏栏叫出来）
        if (!modifierKeyCodes.Contains(VkMenu))
            return virtualKey is VkLWin or VkRWin;

        // 按着 Alt：Alt+F4 关游戏，Alt+Tab / Alt+Esc 把焦点抢走
        return virtualKey is VkF4 or VkTab or VkEscape;
    }
}