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
/// 光标一路禁止符。**已实测**：同一台机器上 XAML 的拖拽探针一条都不响，而本类的回调拿到了完整的
/// <c>DragEnter</c> → <c>Drop</c> 序列，光标也判成了「可放置」。所以这条路是通的。
/// </para>
///
/// <para>
/// <b>它不自己决定收不收</b>：本类只负责「翻译」—— 把 OLE 的屏幕坐标与文件路径交给
/// <see cref="ExternalDropChannel"/>，由那条通道去问页面（见 <see cref="IExternalDropSurface"/>）。
/// 页面才是唯一知道「用户压在哪张卡片上、这一处收不收」的地方。
/// </para>
///
/// <para>
/// <b>光标由本类写出去</b>：<c>effect</c> 出参设成 <c>Copy</c> 系统才画「可放置」，
/// 设成 <c>None</c> 就是禁止符。「光标必须是正确的」这条验收标准落在
/// <see cref="DragEnter"/> / <see cref="DragOver"/> 里。
/// </para>
/// </remarks>
internal sealed class NativeDropTarget : DropTargetInterop.IDropTarget
{
    /// <summary>落点载荷是可放置的文件时给系统的效果位。</summary>
    private const uint AcceptedEffect = DropTargetInterop.DropEffect.Copy;

    /// <summary>落点不接受这次拖放时给系统的效果位（= 光标画禁止符）。</summary>
    private const uint RefusedEffect = DropTargetInterop.DropEffect.None;

    private readonly ILogger _logger;

    /// <summary>这个落点挂在谁身上（写进日志，好区分主窗口 / 浮窗）。</summary>
    private readonly string _owner;

    /// <summary>落点所在的窗口。拖拽进行中要拿它去查窗口树（见 <see cref="LogTreeOnce"/>）。</summary>
    private readonly nint _window;

    /// <summary>
    /// 「这一处收不收」—— 坐标是**屏幕物理像素**，由通道换算后去问页面。
    /// 只在载荷里真有文件时才会被调用（没文件时连问都不用问，直接禁止）。
    /// </summary>
    private readonly Func<int, int, bool> _canAcceptAt;

    /// <summary>真的落下了：把文件路径交给通道去走安装流程。坐标同样是屏幕物理像素。</summary>
    private readonly Func<IReadOnlyList<string>, int, int, Task> _drop;

    /// <summary>本次拖拽里 DragEnter / DragLeave 各打过一眼没有（避免指针反复进出时刷屏）。各用各的旗标 —— 两眼的时刻不同，都要留。</summary>
    private bool _treeLoggedOnEnter;
    private bool _treeLoggedOnLeave;

    /// <summary>上一次算出来的「收不收」——<c>DragOver</c> 每次鼠标移动都会来，那里不必重算命中测试。</summary>
    private bool _lastAccept;

    internal NativeDropTarget(string owner, nint window, ILogger logger,
        Func<int, int, bool> canAcceptAt, Func<IReadOnlyList<string>, int, int, Task> drop)
    {
        _owner = owner;
        _window = window;
        _logger = logger.ForContext<NativeDropTarget>();
        _canAcceptAt = canAcceptAt;
        _drop = drop;
    }

    /// <summary>
    /// 趁拖拽**正在进行**打一眼窗口树。
    ///
    /// <para>
    /// 留着它是因为它是「WinUI 到底什么时候注册落点」的唯一目击证据：实测在关掉 UAC 的机器上，
    /// 启动时 / 启动 8 秒后 / 拖拽进行中 / 拖拽结束之后，整棵树上的落点**始终只有我们挂的那一个** ——
    /// 这解释了为什么那台机器上 XAML 的拖放彻底失效，也说明挂在顶层不会被谁顶掉。
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

        // 光标：**这一步就是整件事的目的**。有文件、且页面说这一处收，才画"可放置"；
        // 其余情况一律老实画禁止符 —— 一律给 Copy 会让用户以为到处都能放，松手却什么都不发生。
        _lastAccept = hasFiles && _canAcceptAt(point.X, point.Y);
        effect = _lastAccept ? AcceptedEffect : RefusedEffect;

        _treeLoggedOnEnter = false;
        _treeLoggedOnLeave = false;

        _logger.Information(
            "[拖放通道] {Owner} DragEnter：文件={HasFiles} 光标={Cursor} 可用格式=[{Formats}] 位置=({X},{Y})",
            _owner, hasFiles ? "有" : "无", _lastAccept ? "可放置" : "禁止", string.Join(",", formats),
            point.X, point.Y);

        LogTreeOnce("拖拽中·DragEnter", ref _treeLoggedOnEnter);

        // 返回 S_OK：我们**处理**了这个事件（返回错误码会让系统认为落点不认这次拖放）。
        return 0;
    }

    int DropTargetInterop.IDropTarget.DragOver(uint keyState, DropTargetInterop.PointL point, ref uint effect)
    {
        // DragOver 以很高频率重复触发（鼠标每动一格一次），所以这里**不重做命中测试、也不记日志**——
        // 重算等于每次鼠标移动都去遍历一次视觉树，而光标本来也是照着上一次的结果画的。
        // 但 effect 每次都必须写：不写的话系统会沿用上一次的值，表现是"划过去禁止符闪一下"。
        effect = _lastAccept ? AcceptedEffect : RefusedEffect;

        return 0;
    }

    int DropTargetInterop.IDropTarget.DragLeave()
    {
        _logger.Information("[拖放通道] {Owner} DragLeave：拖拽离开了落点", _owner);

        // 离开的这一刻再打一眼：若 WinUI 是在拖拽中途把落点挂到子窗口上的，这时应该能看见
        LogTreeOnce("拖拽中·DragLeave", ref _treeLoggedOnLeave);

        _lastAccept = false;

        return 0;
    }

    int DropTargetInterop.IDropTarget.Drop(IDataObject dataObject, uint keyState, DropTargetInterop.PointL point,
        ref uint effect)
    {
        var files = TryReadFilePaths(dataObject);
        var extensions = files
            .Select(static f => Path.GetExtension(f))
            .Where(static e => !string.IsNullOrEmpty(e))
            .ToArray();

        // 只记**个数与扩展名**，不记完整路径：这是要发给用户看 / 用户交回来的日志，
        // 里面的本地路径（用户名、盘符结构）没有诊断价值（见 CLAUDE.md 的"勿暴露本地路径细节"）。
        _logger.Information("[拖放通道] {Owner} Drop：文件数={Count} 扩展名=[{Extensions}] 位置=({X},{Y})",
            _owner, files.Length, string.Join(",", extensions.Distinct(StringComparer.OrdinalIgnoreCase)),
            point.X, point.Y);

        if (files.Length == 0)
        {
            effect = RefusedEffect;
            return 0;
        }

        effect = AcceptedEffect;

        // 处理是异步的（解压、认角色、起向导），而这里是系统在等我们返回 —— **不能阻塞**。
        // 也不能让它抛出去：异常穿过 COM 边界会直接打挂进程，所以整段包住并记日志。
        _ = HandleDropAsync(files, point.X, point.Y);

        return 0;
    }

    /// <summary>把落下这件事交给通道去走安装流程；异常只记日志（见上面那条注释）。</summary>
    private async Task HandleDropAsync(IReadOnlyList<string> files, int screenX, int screenY)
    {
        try
        {
            await _drop(files, screenX, screenY);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[拖放通道] {Owner} 处理落下的文件时出错", _owner);
        }
        finally
        {
            _lastAccept = false;
        }
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
