namespace GIMI_ModManager.Core.Helpers;

/// <summary>
/// <c>RegisterHotKey</c> 的修饰键位。**取值故意与 win32 的 <c>MOD_ALT</c> / <c>MOD_CONTROL</c> /
/// <c>MOD_SHIFT</c> / <c>MOD_WIN</c> 逐位对齐**，这样调用方可以直接强转成 <c>HOT_KEY_MODIFIERS</c>，
/// 不必再写一张映射表（多一张表就多一个对不上的机会）。
/// Core 是 <c>net9.0</c>、拿不到 CsWin32 生成的枚举，所以在这里自己声明一份。
/// </summary>
[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008
}

/// <summary>一个候选热键：修饰键 + 主键 + 主键的显示名（<c>"J"</c> / <c>"F9"</c>）。</summary>
public sealed record OverlayHotkeyCandidate(HotkeyModifiers Modifiers, ushort VirtualKey, string KeyDisplayName)
{
    /// <summary>给人看的写法，如 <c>Ctrl+Alt+J</c>。注册失败时要把候选键名念给用户听，就靠它。</summary>
    public string FriendlyName => OverlayHotkeyCandidates.Describe(Modifiers, KeyDisplayName);
}

/// <summary>尝试注册一个候选键的结果。<paramref name="Reason"/> 是系统给的原因（最常见是「已被占用」）。</summary>
public readonly record struct HotkeyRegistrationAttempt(bool Succeeded, string? Reason)
{
    public static HotkeyRegistrationAttempt Ok { get; } = new(true, null);

    public static HotkeyRegistrationAttempt Failed(string? reason) => new(false, reason);
}

/// <summary>某个候选键没注册上的记录，用于最后给用户一句完整的解释。</summary>
public sealed record OverlayHotkeyFailure(OverlayHotkeyCandidate Candidate, string Reason);

/// <summary>挑选候选热键的结果：挑中的那个（可能为 <c>null</c>）＋ 挑之前失败过的那些。</summary>
public sealed record OverlayHotkeySelection(OverlayHotkeyCandidate? Candidate, IReadOnlyList<OverlayHotkeyFailure> Failures)
{
    public bool IsRegistered => Candidate is not null;

    /// <summary>最终用的不是候选表里的第一个 —— 说明首选被别的程序占了。设置页要把这件事说出来。</summary>
    public bool IsFallback => Candidate is not null && Failures.Count > 0;
}

/// <summary>
/// 浮窗全局热键的候选表与挑选规则（纯逻辑：**不碰 win32**，注册动作由调用方注入，所以可单测）。
///
/// **为什么要有候选表而不是写死一个组合**：<c>RegisterHotKey</c> 是**独占**的，注册上了这个组合
/// 就被系统拦下、不再传给任何程序（包括游戏）。而"某个组合被别的程序占了"是常态而非意外 ——
/// Phase 0 实测本机 <c>Ctrl+Alt+M</c> 就已被占用，于是"提权游戏在前台时热键管不管用"这个问题
/// 当时根本没法验证，失败原因还很容易被误读成"热键功能本身不行"。
///
/// **为什么是组合键而不是单键**：单键（F8 之类）太容易和游戏或输入法抢。
/// </summary>
public static class OverlayHotkeyCandidates
{
    // 主键的虚拟键码。CsWin32 不会生成完备的 VirtualKey 枚举，而 Core 也用不到它，按需声明。
    private const ushort VirtualKeyJ = 0x4A;
    private const ushort VirtualKeyK = 0x4B;
    private const ushort VirtualKeyM = 0x4D;
    private const ushort VirtualKeyF9 = 0x78;

    /// <summary><c>MOD_NOREPEAT</c>：不加的话按住热键会以键盘重复率连续触发，把「切换显隐」刷成不停闪烁。</summary>
    public const uint NoRepeatFlag = 0x4000;

    /// <summary>
    /// 默认候选表，**按顺序尝试、注册第一个可用的**。
    ///
    /// 第一个是 <c>Ctrl+Alt+J</c>（用户选定的默认键）；其余保持 Phase 0 实测时那份列表的顺序，
    /// 后面几个都是当时确认未被占用的组合。
    ///
    /// 返回的是**新数组**而不是缓存的同一个实例：调用方拿到 <c>IReadOnlyList</c> 后仍可以
    /// 强转回数组改建它，返回共享实例会让一次误改污染之后所有调用。
    /// </summary>
    public static IReadOnlyList<OverlayHotkeyCandidate> CreateDefaults() =>
    [
        new(HotkeyModifiers.Control | HotkeyModifiers.Alt, VirtualKeyJ, "J"),
        new(HotkeyModifiers.Control | HotkeyModifiers.Alt, VirtualKeyM, "M"),
        new(HotkeyModifiers.Control | HotkeyModifiers.Alt, VirtualKeyK, "K"),
        new(HotkeyModifiers.Control | HotkeyModifiers.Shift, VirtualKeyJ, "J"),
        new(HotkeyModifiers.Control | HotkeyModifiers.Alt, VirtualKeyF9, "F9")
    ];

    /// <summary>
    /// 转成 <c>RegisterHotKey</c> 的 flags：**必定**带上 <see cref="NoRepeatFlag"/>。
    ///
    /// 这件事必须在共享代码里保证，不能指望每个调用点都记得加 —— 漏了不会报错，
    /// 只会在用户按住热键时表现为浮窗疯狂闪。
    /// </summary>
    public static uint ToRegisterHotKeyFlags(HotkeyModifiers modifiers) => (uint)modifiers | NoRepeatFlag;

    /// <summary>
    /// 拼出人看的写法。修饰键顺序固定为 Ctrl、Alt、Shift、Win，
    /// **不跟着 <see cref="HotkeyModifiers"/> 的位序走** —— 否则同一组键在不同代码路径下会显示成两种写法。
    /// </summary>
    public static string Describe(HotkeyModifiers modifiers, string keyDisplayName)
    {
        var parts = new List<string>(4);

        if (modifiers.HasFlag(HotkeyModifiers.Control))
            parts.Add("Ctrl");
        if (modifiers.HasFlag(HotkeyModifiers.Alt))
            parts.Add("Alt");
        if (modifiers.HasFlag(HotkeyModifiers.Shift))
            parts.Add("Shift");
        if (modifiers.HasFlag(HotkeyModifiers.Win))
            parts.Add("Win");

        parts.Add(keyDisplayName);
        return string.Join("+", parts);
    }

    /// <summary>
    /// 逐个尝试候选键，返回第一个注册成功的。
    ///
    /// <paramref name="tryRegister"/> 由调用方注入（真实现是 <c>RegisterHotKey</c>），
    /// Core 只负责"按什么顺序试、怎么记账"—— 这样这段挑选逻辑才测得了。
    /// </summary>
    public static OverlayHotkeySelection TrySelect(
        IReadOnlyList<OverlayHotkeyCandidate> candidates,
        Func<OverlayHotkeyCandidate, HotkeyRegistrationAttempt> tryRegister)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(tryRegister);

        var failures = new List<OverlayHotkeyFailure>();

        foreach (var candidate in candidates)
        {
            var attempt = tryRegister(candidate);
            if (attempt.Succeeded)
                return new OverlayHotkeySelection(candidate, failures);

            failures.Add(new OverlayHotkeyFailure(candidate,
                string.IsNullOrWhiteSpace(attempt.Reason) ? "系统没有给出原因" : attempt.Reason));
        }

        return new OverlayHotkeySelection(null, failures);
    }

    /// <summary>
    /// 一个候选都没注册上时给用户看的一句话。
    /// 把每个候选键和它各自的原因都列出来 —— 只说一句"热键不可用"，用户既不知道是谁占的、
    /// 也不知道去哪儿改。
    /// </summary>
    public static string DescribeFailure(IReadOnlyList<OverlayHotkeyFailure> failures)
    {
        if (failures.Count == 0)
            return "没有可用的候选热键。";

        return "所有候选热键都注册不上：" +
               string.Join("；", failures.Select(f => $"{f.Candidate.FriendlyName}（{f.Reason}）"));
    }
}