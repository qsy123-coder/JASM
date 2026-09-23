using Microsoft.UI.Xaml;

namespace JASM.OverlaySpike;

/// <summary>
/// 原型的最小宿主。不引 DI / 不引 Serilog / 不引本地化 ——
/// 原型要验证的只有窗口行为，任何多余的依赖都只是构建失败的风险来源。
/// </summary>
public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();

        // 三个层级的异常都要接住并落盘。
        // "点了一下窗口就自己消失了"这种报告，在没有日志的情况下完全无法定位 ——
        // 究竟是崩溃退出、还是被隐藏了，日志里会写得明明白白。
        UnhandledException += (_, e) =>
        {
            SpikeLog.Write($"!!! 未处理异常 (XAML 线程): {e.Exception}");
            SpikeLog.Write($"!!! 指望它自己崩掉以便暴露问题，不设 Handled=true");
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            SpikeLog.Write($"!!! 未处理异常 (CLR 级, 通常意味着进程即将终止): {e.ExceptionObject}");

        TaskScheduler.UnobservedTaskException += (_, e) =>
            SpikeLog.Write($"!!! 未观察的任务异常: {e.Exception}");

        // 进程退出的记录。若日志末尾出现这一行，就说明窗口"消失"其实是**进程结束了**，
        // 而不是窗口被隐藏 —— 这两者在屏幕上看不出区别，但排查方向完全相反。
        AppDomain.CurrentDomain.ProcessExit += (_, _) => SpikeLog.Write("进程正在退出 (ProcessExit)");

        SpikeLog.StartSession();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        SpikeLog.Write("OnLaunched 开始");
        _window = new MainWindow();
        SpikeLog.Write("MainWindow 构造完成，准备 Activate");
        _window.Activate();
        SpikeLog.Write("已 Activate，进入消息循环");
    }
}