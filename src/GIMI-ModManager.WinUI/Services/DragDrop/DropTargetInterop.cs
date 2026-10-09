using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using IDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace GIMI_ModManager.WinUI.Services.DragDrop;

/// <summary>
/// OLE 拖放落点（<c>IDropTarget</c>）所需的最小原生互操作定义。
///
/// <para>
/// <b>为什么这里不写进 <c>NativeMethods.txt</c>、也不走 CsWin32</b>：本文件要用的不只是「调一个 API」，
/// 还要**自己实现 <c>IDropTarget</c> 交给系统回调**（COM 可调用包装，CCW）。CsWin32 生成的那套
/// <c>PInvoke.RegisterDragDrop</c> 参数类型是它自己生成的接口，和这里手写的 <c>[ComImport]</c> 接口
/// 接不上；两边都留一份只会让人分不清哪个在真正做事。所以这一块整体自洽地手写，
/// 其余 win32 调用照旧走 CsWin32（见 <c>Services/Overlay/OverlayWindowStyles.cs</c>）。
/// </para>
///
/// <para>
/// <c>IDataObject</c> 直接用 BCL 的 <see cref="System.Runtime.InteropServices.ComTypes.IDataObject"/>，
/// 不自己再声明一份 —— 它是最标准的那个（<c>0000010e-…</c>），BCL 里就有，能省一层手写。
/// </para>
/// </summary>
internal static class DropTargetInterop
{
    /// <summary>
    /// <c>CF_HDROP</c>：拖放载荷里「文件列表」的剪贴板格式号。
    ///
    /// 它就是 15，不是某个枚举的成员 —— 这个数字是剪贴板格式的注册约定（1..17 是系统预定义的），
    /// 所有处理文件拖放的代码都写死 15。写成常量并注明，是为了让读的人不必去翻 winuser.h。
    /// </summary>
    internal const short CfHDrop = 15;

    /// <summary><c>DragQueryFile</c> 取「有多少个文件」时传的索引哨兵。</summary>
    internal const uint DragQueryFileCount = 0xFFFFFFFF;

    /// <summary>
    /// <c>RegisterDragDrop</c> 在「这个窗口已经注册过落点」时返回的 HRESULT（<c>DRAGDROP_E_ALREADYREGISTERED</c>）。
    ///
    /// 探针用这个值来判断 **WinUI 自己有没有在同一个 HWND 上注册过落点** —— 它是零风险的：
    /// 我们只「尝试注册并读返回值」，不去 <c>Revoke</c> 别人的，所以不可能弄坏现有的拖放。
    /// </summary>
    internal const int DragDropAlreadyRegistered = unchecked((int)0x80040101);

    /// <summary><c>DROPEFFECT_*</c>。落点通过出参返回它来决定光标显示成「复制 / 移动 / 禁止」。</summary>
    internal static class DropEffect
    {
        internal const uint None = 0;
        internal const uint Copy = 1;
        internal const uint Move = 2;
        internal const uint Link = 4;
    }

    /// <summary>
    /// <c>POINTL</c>：OLE 传落点的坐标。**不要用 BCL 的 <c>POINT</c>** —— 那个是 win32 的
    /// <c>POINT</c>（<c>LONG</c> 字段名 x/y），与 OLE 的 <c>POINTL</c> 虽然布局相同但是两个类型名，
    /// 混用后签名对不上时编译器不会帮你。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PointL
    {
        internal int X;
        internal int Y;
    }

    /// <summary>
    /// OLE 拖放落点接口。方法**顺序不可改**（COM 按 vtable 顺序分发），签名与 <c>oleidl.h</c> 一一对应。
    ///
    /// <para>
    /// 四个方法都用 <c>[PreserveSig]</c>：默认的 HRESULT 翻译会把「返回失败码」变成抛异常，
    /// 而这里是系统在调我们，抛出去会穿过 COM 边界 —— 只能自己返回码。
    /// </para>
    ///
    /// <para>
    /// <c>effect</c> 是 <c>ref</c>：进来时是系统建议的效果，我们可以改写它（改写 = 告诉系统光标该画成什么）。
    /// <b>「光标必须是可放置」这个验收标准，靠的就是在 <c>DragEnter</c>/<c>DragOver</c> 里把它设成
    /// <c>Copy</c>。</b>设成 <c>None</c>（或保持 <c>None</c>）= 系统画禁止符。
    /// </para>
    /// </summary>
    [ComImport]
    [Guid("00000122-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDropTarget
    {
        [PreserveSig]
        int DragEnter([MarshalAs(UnmanagedType.Interface)] IDataObject dataObject,
            uint keyState, PointL point, ref uint effect);

        [PreserveSig]
        int DragOver(uint keyState, PointL point, ref uint effect);

        [PreserveSig]
        int DragLeave();

        [PreserveSig]
        int Drop([MarshalAs(UnmanagedType.Interface)] IDataObject dataObject,
            uint keyState, PointL point, ref uint effect);
    }

    /// <summary>
    /// 在当前线程初始化 OLE。<c>RegisterDragDrop</c> 的前提，没初始化会直接失败。
    ///
    /// 返回 <c>S_FALSE</c>（0x00000001）表示「本线程已经初始化过了」，这是**正常**结果不是错误 ——
    /// WinUI 的 UI 线程本来就是 STA，多半已经初始化过。所以判据是 <c>hr &gt;= 0</c> 而不是 <c>== S_OK</c>。
    /// </summary>
    [DllImport("ole32.dll", ExactSpelling = true)]
    internal static extern int OleInitialize(nint reserved);

    /// <summary>与 <see cref="OleInitialize"/> 配对。只有**本次调用真的初始化了**（返回 S_OK）才该调它。</summary>
    [DllImport("ole32.dll", ExactSpelling = true)]
    internal static extern void OleUninitialize();

    /// <summary>
    /// 把 <paramref name="dropTarget"/> 注册为窗口的拖放落点。
    /// <paramref name="dropTarget"/> 传的是 <c>IDropTarget*</c>（用 <c>Marshal.GetComInterfaceForObject</c> 换来的）。
    /// </summary>
    [DllImport("ole32.dll", ExactSpelling = true)]
    internal static extern int RegisterDragDrop(nint window, nint dropTarget);

    /// <summary>注销窗口的落点。**只对我们自己成功注册过的窗口调用**，绝不碰别人的。</summary>
    [DllImport("ole32.dll", ExactSpelling = true)]
    internal static extern int RevokeDragDrop(nint window);

    /// <summary>释放 <c>IDataObject::GetData</c> 交出来的 <c>STGMEDIUM</c>。不释放会漏掉一块全局内存。</summary>
    [DllImport("ole32.dll", ExactSpelling = true)]
    internal static extern void ReleaseStgMedium(ref STGMEDIUM medium);

    /// <summary>
    /// 从 <c>HDROP</c> 里取文件名。
    /// <paramref name="index"/> 传 <see cref="DragQueryFileCount"/> 时返回**文件个数**，其余返回该文件的字符数（不含终止符）。
    /// </summary>
    // ExactSpelling = true 时 DllImport **不会**自动补 A/W 后缀，所以入口点名必须自己写全。
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "DragQueryFileW")]
    internal static extern uint DragQueryFile(nint drop, uint index, [Out] char[]? fileName, uint cch);

    /// <summary>
    /// 读窗口上的 <c>SetProp</c> 属性。
    ///
    /// <para>
    /// <b>这是探针里最能说明问题的一查</b>：OLE 的 <c>RegisterDragDrop</c> 成功时，会在窗口上挂两个属性 ——
    /// <c>OleDropTargetInterface</c>（落点接口指针）与 <c>OleEndPointID</c>（走 broker 那条路时才有）。
    /// 也就是说，**不用真的拖一次**，光查这两个属性就能知道「这个窗口到底有没有落点、是谁的」。
    /// 在库里拖不动的时候，这条比看光标有用得多：光标只告诉你"没接住"，属性告诉你"接的人在不在"。
    /// </para>
    /// </summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "GetPropW")]
    internal static extern nint GetProp(nint window, string name);

    /// <summary>取窗口类名（用来一眼认出内容是挂在 <c>Microsoft.UI.Content.DesktopChildSiteBridge</c> 那类窗口上）。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "GetClassNameW")]
    internal static extern int GetClassName(nint window, [Out] char[] className, int maxCount);

    /// <summary>
    /// 屏幕坐标 → 窗口客户区坐标。
    /// 命中测试要的是 **XAML 的 DIP 坐标**，与屏幕坐标差着「窗口位置」和「DPI 缩放」两道，必须转。
    /// </summary>
    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern bool ScreenToClient(nint window, ref PointL point);

    /// <summary>
    /// 取光标的**屏幕**坐标。
    ///
    /// <para>
    /// <b>为什么宁可问光标、也不用 OLE 回调给的那个 <c>pt</c></b>：真机实测，
    /// 拿 OLE 的 <c>pt</c> 当屏幕坐标去 <c>ScreenToClient</c>，**换算出来的点全贴在窗口最左边缘**
    /// （DIP x 恒为 0～20，命中链永远落在导航栏的 <c>ItemsRepeaterScrollHost</c> 上），
    /// 而用户明明拖在窗口中间 —— 说明它与本进程 <c>ScreenToClient</c> 用的不是同一个坐标系
    /// （跨进程 / DPI 视角差异，具体成因不值得再查）。
    /// </para>
    ///
    /// <para>
    /// <c>GetCursorPos</c> 与本进程的 <c>ScreenToClient</c> **天然同一坐标系** ——
    /// 挂载时那次窗口中心自测走的就是这条路，一直算得对。拖拽期间光标就在落点上，
    /// 所以拿它代替 <c>pt</c> 是等价的。
    /// </para>
    /// </summary>
    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern bool GetCursorPos(out PointL point);

    /// <summary>遍历一个窗口的全部后代（含孙辈）。</summary>
    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern bool EnumChildWindows(nint parent, EnumChildProc callback, nint param);

    /// <summary><see cref="EnumChildWindows"/> 的回调；返回 <c>false</c> 会中止遍历。</summary>
    internal delegate bool EnumChildProc(nint window, nint param);

    /// <summary>OLE 在窗口上留下的「落点接口」属性名。</summary>
    internal const string OleDropTargetInterfaceProp = "OleDropTargetInterface";

    /// <summary>OLE 走 broker 那条路时另留的属性名（见 <c>ole32</c> 的 <c>PrivDragDrop</c>）。</summary>
    internal const string OleEndPointIdProp = "OleEndPointID";

    /// <summary>
    /// 把一棵窗口树描述成若干行，每个窗口标注它有没有 OLE 落点。
    ///
    /// <para>
    /// <b>为什么要在拖拽进行中也要打这个</b>：实测（开发机）在启动时、启动 8 秒后、以及一次拖拽**结束之后**，
    /// 整棵树上的落点都只有我们挂的那一个 —— 可 XAML 的 <c>DragEnter</c> 照样会响。唯一能解释的是
    /// **WinUI 在拖拽进行中才临时注册自己的落点、拖完就撤**。若真如此，我们挂在顶层的那份只会在
    /// 拖拽刚开始时被问一下就顶掉，救不了收不到事件的机器。所以要趁拖拽那一刻抓一眼。
    /// </para>
    /// </summary>
    internal static IReadOnlyList<string> DescribeWindowTree(nint root)
    {
        var rows = new List<string> { DescribeWindow(root, "顶层 ") };

        // EnumChildWindows 连孙辈一起给，平铺即可，不必自己递归
        EnumChildWindows(root, (child, _) =>
        {
            rows.Add(DescribeWindow(child, "子窗口"));
            return true;
        }, 0);

        return rows;
    }

    /// <summary>把一个窗口描述成一行：类名 + 有没有落点接口 / broker 端点属性。</summary>
    private static string DescribeWindow(nint window, string tag)
    {
        var buffer = new char[256];
        var length = GetClassName(window, buffer, buffer.Length);
        var className = length > 0 ? new string(buffer, 0, length) : "<类名读不到>";

        var hasTarget = GetProp(window, OleDropTargetInterfaceProp) != 0;
        var hasEndpoint = GetProp(window, OleEndPointIdProp) != 0;

        return $"    {tag} 0x{window:X} {className} 落点={(hasTarget ? "有" : "无")} 端点={(hasEndpoint ? "有" : "无")}";
    }
}