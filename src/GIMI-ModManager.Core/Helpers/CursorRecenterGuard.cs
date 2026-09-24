namespace GIMI_ModManager.Core.Helpers;

/// <summary>
/// 判断「光标是不是被游戏挪到了它自己窗口的中心」—— 送完键据此决定要不要把光标放回原处。
///
/// **为什么需要**：F10 必须打到前台窗口上，所以浮窗那条路送键前必然先把游戏切到前台。游戏
/// （原神这类鼠标捕获的玩法）重新拿到焦点时会把系统光标撂到自己窗口的中心 —— 用户的手一动不动，
/// 光标却跑到屏幕中间（游戏占满屏幕时它的中心就是屏幕中心）。后果不只是"指针位置不对"：浮窗那条
/// 「光标压在我身上才拿回前台」的自愈随之失效（光标已经不在浮窗上了），用户得自己把鼠标挪回去。
/// 2026-09-24 实测：连点几次里"有概率"发生，日志里表现为诊断行的「光标下」在游戏窗口和浮窗之间来回跳。
///
/// **判据刻意收得很窄**：只认「位置变了」**且**「新位置就贴在游戏窗口中心」。用户在送键那 1 秒里
/// 自己挪鼠标的情形必须放过 —— 否则就成了把光标往回拽，而"鼠标被拉扯"这类体验用户已经抱怨过。
/// 于是判错的方向是**不还原**（毛病照旧，但日志里留着前后坐标可调），而不是乱动用户的光标。
///
/// 纯逻辑：坐标和容差都由调用方传进来，本类不读时钟、不碰 win32。
/// </summary>
public static class CursorRecenterGuard
{
    /// <summary>
    /// 判定容差（像素）。给得比"刚好命中"宽一点：调用方拿到的是窗口**外框**中心，而游戏报的可能是
    /// **客户区**中心 —— 窗口模式下一根标题栏就能差出十几像素。DPI 虚拟化不用额外宽容差：
    /// 不认 DPI 的游戏按逻辑坐标报的那个点，映射到物理坐标正好还是同一个中心点。
    /// </summary>
    public const int TolerancePixels = 48;

    /// <summary>
    /// 光标是否被挪到了游戏窗口中心（要还原 → <c>true</c>）。
    /// 位置根本没变一律 <c>false</c> —— 那是最常见的情形，不该走还原那条路。
    /// </summary>
    public static bool IsRecenteredToGameCenter(int beforeX, int beforeY, int nowX, int nowY,
        int gameCenterX, int gameCenterY)
    {
        if (beforeX == nowX && beforeY == nowY)
            return false;

        return Math.Abs(nowX - gameCenterX) <= TolerancePixels
               && Math.Abs(nowY - gameCenterY) <= TolerancePixels;
    }
}