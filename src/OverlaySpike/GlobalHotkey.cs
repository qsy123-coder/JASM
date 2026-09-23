using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Windows.System;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using WinUIEx.Messaging;

namespace JASM.OverlaySpike;

/// <summary>
/// 一个候选热键：修饰键 + 主键 + 给人看的说明。说明会直接显示在面板和日志里，
/// 所以失败时用户一眼能看出"是哪个组合键被占了"。
/// </summary>
internal sealed record HotkeyCandidate(HOT_KEY_MODIFIERS Modifiers, VirtualKey Key, string Description);

/// <summary>
/// 一个全局热键的注册、拦截与回收。
///
/// JASM 此前**完全没有**全局热键 —— 全仓库 <c>RegisterHotKey</c> / <c>WM_HOTKEY</c> /
/// 键盘钩子零命中，README 里列的 SPACE / F10 / F5 全是应用内快捷键（要求主窗口有焦点）。
/// 而浮窗的前提恰恰是「游戏占着前台、JASM 没焦点」，所以全局热键是绕不过去的一环，
/// 必须在 Phase 0 就验证它能用。
///
/// 还有一条假设要在实机里确认：鸣潮是 <c>require_admin = true</c>（游戏以管理员权限运行），
/// 而本原型是 asInvoker。理论上 RegisterHotKey 注册的是系统级热键，命中时由系统直接把
/// WM_HOTKEY 投递给注册窗口，不走 SendInput / 前台窗口那一套 UIPI 完整性级别检查，
/// 因此提权游戏在前台时依然应该有效 —— 但这只是理论，见 README 验证项 5。
/// </summary>
internal sealed class GlobalHotkey : IDisposable
{
    /// <summary><c>WM_HOTKEY</c> 的消息号。它是 #define 而非 API，CsWin32 不生成，自己收一个。</summary>
    private const int WmHotkey = 0x0312;

    private readonly WindowMessageMonitor _monitor;
    private readonly Window _window;
    private readonly HWND _hwnd;
    private readonly int _id;
    private bool _registered;
    private bool _disposed;
    private int _messagesSeen;

    /// <summary>热键被按下。回调已在 UI 线程（WM_HOTKEY 由窗口消息循环派发），可直接改界面。</summary>
    public event Action? Pressed;

    /// <summary>注册失败的原因（正常可读的中文/系统文案），成功时为 <c>null</c>。</summary>
    public string? ErrorMessage { get; }

    /// <summary>是否注册成功。失败时 <see cref="ErrorMessage"/> 说明原因（最常见是热键被别的程序占了）。</summary>
    public bool IsRegistered => _registered;

    /// <summary>这个热键的人类可读描述，直接显示在面板上。</summary>
    public string Description { get; }

    /// <summary>
    /// 依次尝试 <paramref name="candidates"/>，注册**第一个可用的**组合键。
    ///
    /// 为什么要有备选列表：本机实测 <c>Ctrl + Alt + M</c> 已经被别的程序占用
    /// （<c>RegisterHotKey</c> 返回热键已注册），原型压根注册不上热键，
    /// 于是"提权游戏前台时热键有没有效"这个问题根本无从验证 —— 失败原因还很容易被误读成
    /// "热键功能不行"。热键被占是常态而非意外，所以这个列表是有意为之。
    /// </summary>
    public GlobalHotkey(Window window, int id, IReadOnlyList<HotkeyCandidate> candidates)
    {
        _window = window;
        _id = id;
        _hwnd = (HWND)WinRT.Interop.WindowNative.GetWindowHandle(window);

        // 消息钩子只挂一次：它跟"最终注册了哪个组合键"无关。
        // 放在循环外面，免得为每个候选键反复替换/还原窗口过程
        // （WinUIEx 的 WindowMessageMonitor 就是靠替换窗口过程实现的）。
        _monitor = new WindowMessageMonitor(window);
        _monitor.WindowMessageReceived += OnWindowMessageReceived;

        var failures = new List<string>();

        foreach (var candidate in candidates)
        {
            // MOD_NOREPEAT：不加的话按住热键会以键盘重复率连续触发，把显隐切换刷成闪烁
            var flags = candidate.Modifiers | HOT_KEY_MODIFIERS.MOD_NOREPEAT;

            if (PInvoke.RegisterHotKey(_hwnd, _id, flags, (uint)candidate.Key))
            {
                _registered = true;
                Description = candidate.Description;

                // 这里不打印 hwnd 的十六进制值：HWND.Value 在 CsWin32 里是 void*，
                // 读它要求 unsafe 上下文，为一行日志把整个类标成 unsafe 不值当。
                SpikeLog.Write($"热键注册成功: {Description}  id={_id}  窗口句柄有效={!_hwnd.IsNull}" +
                               (failures.Count == 0
                                   ? string.Empty
                                   : $"（前 {failures.Count} 个候选被占用: {string.Join("; ", failures)}）"));
                return;
            }

            var reason = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            failures.Add($"{candidate.Description}={reason}");
            SpikeLog.Write($"热键候选不可用: {candidate.Description}  原因={reason}");
        }

        Description = "(一个都没注册上)";
        ErrorMessage = failures.Count == 0
            ? "调用方没有提供任何候选热键"
            : $"所有候选热键都被占用 —— {string.Join("；", failures)}";
        SpikeLog.Write($"热键全部注册失败: {ErrorMessage}");
    }

    private void OnWindowMessageReceived(object? sender, WindowMessageEventArgs e)
    {
        // 头 20 条消息全部记下来，用途是证明「消息钩子确实挂上了」。
        // 少了这个，热键没反应时就没法区分两种情况：
        //   (a) 钩子没生效（连消息都收不到）   (b) 钩子正常，但系统没投递 WM_HOTKEY。
        // 这两种情况的排查方向完全相反 —— 前者是 WinUIEx 的用法问题，后者是 RegisterHotKey 的问题。
        if (_messagesSeen < 20)
        {
            _messagesSeen++;
            SpikeLog.Write($"窗口消息 #{_messagesSeen}: MessageId=0x{e.Message.MessageId:X4} wParam={e.Message.WParam}");
        }

        // 只认自己这个 id 的 WM_HOTKEY：同一个窗口上将来可能注册多个热键。
        if (e.Message.MessageId != WmHotkey)
            return;

        if ((int)e.Message.WParam != _id)
            return;

        SpikeLog.Write($"<<< 收到 WM_HOTKEY id={_id} —— 热键生效，准备切换显隐");
        Pressed?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        // 先摘事件再注销，避免注销过程中还有已在队列里的 WM_HOTKEY 打进来。
        _monitor.WindowMessageReceived -= OnWindowMessageReceived;

        if (_registered)
        {
            PInvoke.UnregisterHotKey(_hwnd, _id);
            _registered = false;
        }
    }
}