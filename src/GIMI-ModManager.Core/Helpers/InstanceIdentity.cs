namespace GIMI_ModManager.Core.Helpers;

/// <summary>
/// 判断「另一个同名进程」到底是不是**本程序自己的另一个实例** —— 启动时的单实例检查据此区分
/// 「用户重复启动了 JASM」（把已有窗口拉到前台、自己退出就好）与「另一个安装的 JASM 也在跑」
/// （不能把对方的窗口拉前台冒充自己的启动）。
///
/// **为什么需要**：单实例检查只看进程名（<c>Process.GetProcessesByName</c>）。JASM 是 fork 出来的，
/// 上游与其他 fork 的产物**进程名同样叫 JASM**，用户机器上同时装着两份是常态。只看名字的后果是：
/// 启动本程序时对方在跑，本程序会把**对方的窗口**拉到前台然后自己退出 —— 用户以为启动成功了，
/// 看到的却是另一个安装的界面（2026-09-24 实机复现：发布版 2.29.0 启动后停在 D:\JASM\ 那份 2.29.1 上）。
///
/// 判据是**映像全路径**：同一个安装必然同路径，不同安装（哪怕同名、同版本、同目录名）路径必然不同。
/// 比较用 <see cref="StringComparison.OrdinalIgnoreCase"/> —— 盘符 / 目录的大小写取决于用户怎么敲的，
/// 不能算成两份。
///
/// **读不到路径时保守判成「自己」**：那一侧退回改动前的行为（拉前台 + 退出），
/// 不会因为一次读路径失败就弹出「另一个 JASM 在跑」这种用户看不懂的提示。
/// </summary>
public static class InstanceIdentity
{
    /// <summary>
    /// 这两个映像路径是不是同一个安装的 JASM。
    /// 任一侧为空 / 空白 / 读不到（进程已退出、权限不足、路径非法）→ <c>true</c>（保守，见类注释）。
    /// </summary>
    public static bool IsSameApp(string? currentImagePath, string? otherImagePath)
    {
        var current = NormalizeImagePath(currentImagePath);
        var other = NormalizeImagePath(otherImagePath);

        if (current is null || other is null)
            return true;

        return string.Equals(current, other, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 归一成可比形式：<see cref="Path.GetFullPath(string)"/> 顺带消化 <c>.</c> / <c>..</c> / 混合分隔符。
    /// 拿不到合法路径返回 <c>null</c>（调用方当「读不到」处理，不抛异常 —— 这里在启动路径上，抛出去就是启动失败）。
    /// </summary>
    private static string? NormalizeImagePath(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
            return null;

        try
        {
            return Path.GetFullPath(imagePath);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException)
        {
            return null;
        }
    }
}