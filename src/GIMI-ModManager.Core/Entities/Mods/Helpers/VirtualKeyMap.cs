namespace GIMI_ModManager.Core.Entities.Mods.Helpers;

/// <summary>
/// 一个按键名的解析结果：UI 显示文本 + （可选的）Windows 虚拟键码。
/// 显示部分与旧 <c>MapKeyName</c> 的返回值逐字一致，虚拟键码是本类新增的。
/// </summary>
/// <param name="Display">UI 显示文本，如 "→"、"小键盘6"、"鼠标左键"、"F10"</param>
/// <param name="IsArrow">是否为方向键（UI 层用箭头图标而非文字展示）</param>
/// <param name="VirtualKeyCode">Windows 虚拟键码；<c>null</c> 表示该按键无法合成发送</param>
public readonly record struct VirtualKeyDefinition(string Display, bool IsArrow, ushort? VirtualKeyCode)
{
    /// <summary>true = 键盘键，可以合成发送。鼠标键、未知键名、无按键值都是 false。</summary>
    public bool CanSend => VirtualKeyCode.HasValue;
}

/// <summary>
/// 3Dmigoto 按键名（<c>VK_RIGHT</c> / 裸 <c>RIGHT</c> / <c>H</c> / <c>VK_NUMPAD6</c>）→ 显示文本 + 虚拟键码。
///
/// 这张表取代了原先只给显示文本的 <c>MapKeyName</c>：显示文案逐条照搬，
/// 同时补上之前被丢弃的虚拟键码，供「点击徽章把按键发给游戏」使用。
/// </summary>
public static class VirtualKeyMap
{
    // Ctrl / Shift / Alt 单独放，不进主表 —— 旧 MapKeyName 对它们走的是「原样显示」的回退分支
    // （Resolve("CTRL") → "CTRL"）。塞进主表会悄悄把显示改成 "Ctrl"，所以分开定义。
    private static readonly VirtualKeyDefinition CtrlKey = new("Ctrl", false, 0x11);
    private static readonly VirtualKeyDefinition ShiftKey = new("Shift", false, 0x10);
    private static readonly VirtualKeyDefinition AltKey = new("Alt", false, 0x12);

    private static readonly IReadOnlyDictionary<string, VirtualKeyDefinition> Map = BuildMap();

    /// <summary>
    /// 解析 ini 里的按键名。词表里没有的名字**原样**返回且虚拟键码为 <c>null</c>
    /// （保持旧 MapKeyName 的兜底行为：显示不变、但不可发送）。
    /// </summary>
    public static VirtualKeyDefinition Resolve(string? keyName)
    {
        if (string.IsNullOrWhiteSpace(keyName))
            return new VirtualKeyDefinition(keyName ?? string.Empty, false, null);

        // 注意返回的是 ORIGINAL 而非 trim 后的：旧 MapKeyName 的兜底分支返回的也是原始 keyName
        return Map.TryGetValue(keyName.Trim(), out var definition)
            ? definition
            : new VirtualKeyDefinition(keyName, false, null);
    }

    /// <summary>
    /// 解析修饰键 token（<c>ctrl</c> / <c>shift</c> / <c>alt</c>，大小写不敏感）。
    /// <c>no_ctrl</c> / <c>no_shift</c> / <c>no_alt</c> 是 3Dmigoto 的「禁止条件」语义，**不是**修饰键，
    /// 因此这里返回 false，调用方不得把它们当成需要按下的键。
    /// </summary>
    public static bool TryResolveModifier(string? token, out VirtualKeyDefinition definition)
    {
        switch (token?.Trim().ToLowerInvariant())
        {
            case "ctrl":
                definition = CtrlKey;
                return true;
            case "shift":
                definition = ShiftKey;
                return true;
            case "alt":
                definition = AltKey;
                return true;
            default:
                definition = default;
                return false;
        }
    }

    private static IReadOnlyDictionary<string, VirtualKeyDefinition> BuildMap()
    {
        var map = new Dictionary<string, VirtualKeyDefinition>(StringComparer.OrdinalIgnoreCase);

        // 别名重复会让 Resolve 的结果取决于注册顺序，属于静默错误 —— 直接在这里炸掉
        void Add(VirtualKeyDefinition definition, params string[] aliases)
        {
            foreach (var alias in aliases)
            {
                if (!map.TryAdd(alias, definition))
                    throw new InvalidOperationException($"按键别名重复注册: {alias}");
            }
        }

        // ── 方向键 ──
        Add(new VirtualKeyDefinition("→", true, 0x27), "VK_RIGHT", "RIGHT");
        Add(new VirtualKeyDefinition("←", true, 0x25), "VK_LEFT", "LEFT");
        Add(new VirtualKeyDefinition("↑", true, 0x26), "VK_UP", "UP");
        Add(new VirtualKeyDefinition("↓", true, 0x28), "VK_DOWN", "DOWN");

        // ── 鼠标键：故意不给虚拟键码 —— 本功能不模拟鼠标（模拟点击会在光标处发真实点击，风险太大）──
        Add(new VirtualKeyDefinition("鼠标左键", false, null), "VK_LBUTTON", "LBUTTON");
        Add(new VirtualKeyDefinition("鼠标右键", false, null), "VK_RBUTTON", "RBUTTON");
        Add(new VirtualKeyDefinition("鼠标中键", false, null), "VK_MBUTTON", "MBUTTON");

        // ── 功能键 F1..F12 = 0x70..0x7B ──
        for (var i = 1; i <= 12; i++)
            Add(new VirtualKeyDefinition($"F{i}", false, (ushort)(0x6F + i)), $"VK_F{i}", $"F{i}");

        // ── 特殊键 ──
        Add(new VirtualKeyDefinition("空格", false, 0x20), "VK_SPACE", "SPACE");
        Add(new VirtualKeyDefinition("回车", false, 0x0D), "VK_RETURN", "RETURN", "ENTER");
        Add(new VirtualKeyDefinition("Tab", false, 0x09), "VK_TAB", "TAB");
        Add(new VirtualKeyDefinition("Esc", false, 0x1B), "VK_ESCAPE", "ESCAPE", "ESC");
        Add(new VirtualKeyDefinition("退格", false, 0x08), "VK_BACK", "BACKSPACE");
        Add(new VirtualKeyDefinition("Delete", false, 0x2E), "VK_DELETE", "DELETE");
        Add(new VirtualKeyDefinition("Home", false, 0x24), "VK_HOME");
        Add(new VirtualKeyDefinition("End", false, 0x23), "VK_END");
        Add(new VirtualKeyDefinition("PgUp", false, 0x21), "VK_PRIOR", "PAGEUP");
        Add(new VirtualKeyDefinition("PgDn", false, 0x22), "VK_NEXT", "PAGEDOWN");
        // 旧表漏了 Insert，导致 ini 里的 VK_INSERT 显示成原始名 "VK_INSERT" 且无法发送。
        // 唯一有意为之的显示变化：VK_INSERT / INSERT 从 "VK_INSERT" 变成 "Insert"。
        Add(new VirtualKeyDefinition("Insert", false, 0x2D), "VK_INSERT", "INSERT");

        // ── 小键盘 0..9 = 0x60..0x69 ──
        for (var i = 0; i <= 9; i++)
            Add(new VirtualKeyDefinition($"小键盘{i}", false, (ushort)(0x60 + i)), $"VK_NUMPAD{i}", $"NUMPAD{i}");

        // ── 字母 / 数字：虚拟键码就是 ASCII（VK_A..VK_Z = 0x41..0x5A，VK_0..VK_9 = 0x30..0x39）──
        for (var c = 'A'; c <= 'Z'; c++)
            Add(new VirtualKeyDefinition(c.ToString(), false, (ushort)c), c.ToString(), "VK_" + c);
        for (var c = '0'; c <= '9'; c++)
            Add(new VirtualKeyDefinition(c.ToString(), false, (ushort)c), c.ToString(), "VK_" + c);

        // ── OEM 符号键（别名集合与旧表逐条一致：其中 VK_OEM_1..7 没有裸别名）──
        Add(new VirtualKeyDefinition(".", false, 0xBE), "VK_OEM_PERIOD", "OEM_PERIOD");
        Add(new VirtualKeyDefinition(",", false, 0xBC), "VK_OEM_COMMA", "OEM_COMMA");
        Add(new VirtualKeyDefinition("-", false, 0xBD), "VK_OEM_MINUS", "OEM_MINUS");
        Add(new VirtualKeyDefinition("=", false, 0xBB), "VK_OEM_PLUS", "OEM_PLUS");
        Add(new VirtualKeyDefinition(";", false, 0xBA), "VK_OEM_1");
        Add(new VirtualKeyDefinition("/", false, 0xBF), "VK_OEM_2");
        Add(new VirtualKeyDefinition("`", false, 0xC0), "VK_OEM_3");
        Add(new VirtualKeyDefinition("[", false, 0xDB), "VK_OEM_4");
        Add(new VirtualKeyDefinition("\\", false, 0xDC), "VK_OEM_5");
        Add(new VirtualKeyDefinition("]", false, 0xDD), "VK_OEM_6");
        Add(new VirtualKeyDefinition("'", false, 0xDE), "VK_OEM_7");

        // ── 显式的「无按键」──
        Add(new VirtualKeyDefinition("(无)", false, null), "NONE", "DISABLED");

        return map;
    }
}