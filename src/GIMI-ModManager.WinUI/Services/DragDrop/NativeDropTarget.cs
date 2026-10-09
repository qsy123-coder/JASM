using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Serilog;
using IDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace GIMI_ModManager.WinUI.Services.DragDrop;

/// <summary>
/// 自己实现的 OLE 落点：系统把拖放回调打到这里来。
///
/// <para>
/// <b>它要解决什么</b>：在关掉 UAC 的机器上（<c>EnableLUA=0</c>，网吧无盘机常见），
/// 所有进程都以高完整性运行，此时 **WinUI 3 收不到外部拖放** —— 表现是 <c>DragEnter</c> 从不触发、
/// 光标一路禁止符，而 JASM 启动时的「与 shell 同级」策略恰好会保持高完整性，必然踩中。
/// 同一个环境下 WPF / WinForms 的拖放却是正常的，说明系统层面并没有禁止拖放，
/// 坏的是框架宿主 OLE 落点的方式 —— 所以这里绕开框架，自己实现一份落点。
/// </para>
///
/// <para>
/// <b>Phase 0 阶段它只记录、不改行为</b>：回调里只写日志，落点既不改写业务、也不接管现有的 XAML 落点。
/// 见 <see cref="DropTargetProbe"/> 的类注释（为什么先探针、以及为什么不停掉别人的注册）。
/// </para>
///
/// <para>
/// <b>光标由本类决定</b>：<c>effect</c> 出参设成 <c>Copy</c> 系统才画「可放置」，
/// 设成 <c>None</c> 就是禁止符。「光标必须是正确的」这条验收标准落在
/// <see cref="DragEnter"/> / <see cref="DragOver"/> 里。
/// </para>
/// </remarks>
internal sealed class NativeDropTarget : DropTargetInterop.IDropTarget
{
    /// <summary>落点载荷是可放置的文件时给系统的效果位。</summary>
    private const uint AcceptedEffect = DropTargetInterop.DropEffect.Copy;

    private readonly ILogger _logger;

    /// <summary>这个落点挂在谁身上（写进日志，好区分主窗口 / 浮窗）。</summary>
    private readonly string _owner;

    /// <summary>落点所在的窗口。拖拽进行中要拿它去查窗口树（见 <see cref="LogTreeOnce"/>）。</summary>
    private readonly nint _window;

    /// <summary>本次拖放里有没有出现过载荷 —— 用来让 <c>DragLeave</c> / <c>Drop</c> 的日志能对上号。</summary>
    private bool _dragging;

    /// <summary>本次拖拽里 DragEnter / DragLeave 各打过一眼没有（避免指针反复进出时刷屏）。各用各的旗标 —— 两眼的时刻不同，都要留。</summary>
    private bool _treeLoggedOnEnter;
    private bool _treeLoggedOnLeave;

    internal NativeDropTarget(string owner, nint window, ILogger logger)
    {
        _owner = owner;
        _window = window;
        _logger = logger.ForContext<NativeDropTarget>();
    }

    /// <summary>
    /// 趁拖拽**正在进行**打一眼窗口树。
    ///
    /// <para>
    /// 为什么非要在这一刻打：开发机上「启动时 / 启动 8 秒后 / 一次拖拽结束之后」三种时刻，
    /// 整棵树上的落点都只有我们挂的那一个，可 XAML 的 <c>DragEnter</c> 照样会响 ——
    /// 唯一讲得通的是 **WinUI 在拖拽进行中才临时注册自己的落点、拖完就撤**。
    /// 若确实如此，我们挂在顶层的落点只在拖拽开始时被问一下就顶掉，
    /// 收不到拖放的机器上照样救不回来 —— 那这条路就得推翻重做。
    /// </para>
    /// </summary>
    private void LogTreeOnce(string phase, ref bool alreadyLogged)
    {
        if (alreadyLogged)
            return;

        alreadyLogged = true;

        _logger.Information("[拖放通道] {Owner} {Phase} 时的窗口树：\n{Tree}",
            _owner, phase, string.Join("\n", DropTargetInterop.DescribeWindowTree(_window)));
    }

    int DropTargetInterop.IDropTarget.DragEnter(IDataObject dataObject, uint keyState, DropTargetInterop.PointL point,
        ref uint effect)
    {
        var hasFiles = TryProbeFilePayload(dataObject, out var formats);

        // 先定光标：**这一步就是整件事的目的**。有文件就画"可放置"，没有就老实画禁止符
        // （不能一律给 Copy —— 那会让用户以为虚拟文件/URL 也能拖进来，然后松手没反应）。
        effect = hasFiles ? AcceptedEffect : DropTargetInterop.DropEffect.None;

        _dragging = hasFiles;
        _treeLoggedOnEnter = false;
        _treeLoggedOnLeave = false;

        _logger.Information(
            "[拖放通道] {Owner} DragEnter：文件={HasFiles} 光标={Cursor} 可用格式=[{Formats}] 位置=({X},{Y})",
            _owner, hasFiles ? "有" : "无", hasFiles ? "可放置" : "禁止", string.Join(",", formats),
            point.X, point.Y);

        LogTreeOnce("拖拽中·DragEnter", ref _treeLoggedOnEnter);

        // 返回 S_OK：我们**处理**了这个事件（返回错误码会让系统认为落点不认这次拖放）。
        return 0;
    }

    int DropTargetInterop.IDropTarget.DragOver(uint keyState, DropTargetInterop.PointL point, ref uint effect)
    {
        // DragOver 会以很高的频率重复触发（鼠标每动一格一次）。这里 **不记日志** ——
        // 之前拖拽探针就因为不打点被日志刷屏吃过亏（见 Helpers/DragProbe.cs 的节流注释）。
        // 光标仍然每次都要设：不设的话系统会沿用上一次的值，表现是"进得来但划过去禁止符闪一下"。
        effect = _dragging ? AcceptedEffect : DropTargetInterop.DropEffect.None;

        return 0;
    }

    int DropTargetInterop.IDropTarget.DragLeave()
    {
        _logger.Information("[拖放通道] {Owner} DragLeave：拖拽离开了落点", _owner);

        // 离开的这一刻再打一眼：若 WinUI 是在拖拽中途把落点挂到子窗口上的，这时应该能看见
        LogTreeOnce("拖拽中·DragLeave", ref _treeLoggedOnLeave);

        _dragging = false;

        return 0;
    }

    int DropTargetInterop.IDropTarget.Drop(IDataObject dataObject, uint keyState, DropTargetInterop.PointL point,
        ref uint effect)
    {
        // Phase 0：只记录，**不接管**。真正安装要走现有的落点判据（浮窗只收真文件、详情页一次只收一个包…），
        // 那是接完线之后的事 —— 现在贸然在这里调安装流程，会出现"同一个文件被处理两次"。
        var files = TryReadFilePaths(dataObject);
        var extensions = files
            .Select(static f => Path.GetExtension(f))
            .Where(static e => !string.IsNullOrEmpty(e))
            .ToArray();

        effect = files.Length > 0 ? AcceptedEffect : DropTargetInterop.DropEffect.None;
        _dragging = false;

        // 只记**个数与扩展名**，不记完整路径：这是要发给用户看 / 用户交回来的日志，
        // 里面的本地路径（用户名、盘符结构）没有诊断价值（见 CLAUDE.md 的"勿暴露本地路径细节"）。
        _logger.Information("[拖放通道] {Owner} Drop：文件数={Count} 扩展名=[{Extensions}] 位置=({X},{Y})",
            _owner, files.Length, string.Join(",", extensions.Distinct(StringComparer.OrdinalIgnoreCase)),
            point.X, point.Y);

        return 0;
    }

    /// <summary>
    /// 载荷里有没有可放置的文件；顺带把载荷声明的格式清单带出来（报告里最有用的那一条线索）。
    /// </summary>
    private static bool TryProbeFilePayload(IDataObject dataObject, out string[] formats)
    {
        var format = FileFormat();
        formats = DescribeFormats(dataObject);

        try
        {
            return dataObject.QueryGetData(ref format) == 0;
        }
        catch (Exception)
        {
            // 延迟渲染的数据对象：读格式可能抛（来源进程忙 / 已退出）。**这是诊断路径，不能反过来打断拖拽**。
            return false;
        }
    }

    /// <summary>取出载荷里的文件路径。取不到返回空数组（绝不抛）。</summary>
    private static string[] TryReadFilePaths(IDataObject dataObject)
    {
        var format = FileFormat();

        try
        {
            dataObject.GetData(ref format, out var medium);

            try
            {
                // CF_HDROP 一律是全局内存句柄；tymed 不是 HGLOBAL 时 unionmember 不是 HDROP，读了就是野指针
                if (medium.tymed != TYMED.TYMED_HGLOBAL || medium.unionmember == IntPtr.Zero)
                    return [];

                return ReadFilePaths(medium.unionmember);
            }
            finally
            {
                // 必须在 finally：GetData 成功却中途返回（比如上面那条 tymed 分支）时同样要还这块全局内存，
                // 否则每拖一次就漏一块
                DropTargetInterop.ReleaseStgMedium(ref medium);
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "[拖放通道] 读取拖放载荷失败");
            return [];
        }
    }

    /// <summary>从 <c>HDROP</c> 把路径一个个取出来。</summary>
    private static string[] ReadFilePaths(nint drop)
    {
        var count = DropTargetInterop.DragQueryFile(drop, DropTargetInterop.DragQueryFileCount, null, 0);
        var paths = new List<string>((int)count);

        for (uint i = 0; i < count; i++)
        {
            // 两趟调用：第一趟问长度，第二趟才取内容（先按一个固定大小去取会截断长路径）
            var length = DropTargetInterop.DragQueryFile(drop, i, null, 0);
            if (length == 0)
                continue;

            // 缓冲区要比长度多一个字符放终止符：DragQueryFile 会把长度算成不含终止符
            var buffer = new char[length + 1];
            if (DropTargetInterop.DragQueryFile(drop, i, buffer, (uint)buffer.Length) == 0)
                continue;

            paths.Add(new string(buffer, 0, (int)length));
        }

        return paths.ToArray();
    }

    /// <summary>把载荷声明的格式清单读成人话（<c>CF_HDROP</c> / <c>FileNameW</c> / 自定义格式号）。</summary>
    private static string[] DescribeFormats(IDataObject dataObject)
    {
        try
        {
            var enumerator = (IEnumFORMATETC)dataObject.EnumFormatEtc(DATADIR.DATADIR_GET);
            if (enumerator == null)
                return [];

            var buffer = new FORMATETC[1];
            var formats = new List<string>();

            // 第三参是「实际取到了几个」的出参。一次只取一个，COM 规定这时可以传 NULL ——
            // 传 null 也比传一个数组省一次分配；返回值 0 = 还取到了一个，非 0 = 取完了。
            while (enumerator.Next(1, buffer, null) == 0)
                formats.Add(DescribeFormat(buffer[0].cfFormat));

            return formats.ToArray();
        }
        catch (Exception)
        {
            // 同上：格式清单只是线索，读不到就不给，不能因此打断拖拽
            return [];
        }
    }

    /// <summary>把一个剪贴板格式号说成名字。只认几个常见的，其余原样报数字 —— 免得瞎猜。</summary>
    private static string DescribeFormat(short format) => format switch
    {
        DropTargetInterop.CfHDrop => "CF_HDROP",
        13 => "CF_UNICODETEXT",
        1 => "CF_TEXT",
        3 => "CF_METAFILEPICT",
        8 => "CF_DIB",
        17 => "CF_DIBV5",
        _ => $"fmt{format}"
    };

    /// <summary>「要的是文件列表」的那份 <c>FORMATETC</c> 请求。</summary>
    private static FORMATETC FileFormat() => new()
    {
        cfFormat = DropTargetInterop.CfHDrop,
        ptd = IntPtr.Zero,
        dwAspect = DVASPECT.DVASPECT_CONTENT,
        lindex = -1,
        tymed = TYMED.TYMED_HGLOBAL
    };
}