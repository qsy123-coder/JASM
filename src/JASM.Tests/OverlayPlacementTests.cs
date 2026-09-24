using GIMI_ModManager.Core.Helpers;

namespace JASM.Tests;

/// <summary>
/// Covers <see cref="OverlayPlacement"/> — 浮窗初始/恢复位置。
///
/// 浮窗没有标题栏，一旦被摆到工作区之外就**再也拖不回来**，所以"夹回可见区域"不是防御性代码，
/// 而是这个功能能不能用下去的前提（换显示器、拔副屏、改缩放都会让上次存的坐标失配）。
/// </summary>
public class OverlayPlacementTests
{
    private const int WindowWidth = 470;
    private const int WindowHeight = 440;

    /// <summary>主显示器 + 任务栏占掉 40px 的典型工作区。</summary>
    private static readonly OverlayRect PrimaryWorkArea = new(0, 0, 1920, 1040);

    [Fact]
    public void CentersInsideTheWorkAreaWhenThereIsNoSavedPosition()
    {
        var (x, y) = OverlayPlacement.ResolveTopLeft(null, PrimaryWorkArea, WindowWidth, WindowHeight);

        Assert.Equal((1920 - WindowWidth) / 2, x);
        Assert.Equal((1040 - WindowHeight) / 2, y);
    }

    [Fact]
    public void KeepsASavedPositionThatIsStillOnScreen()
    {
        var (x, y) = OverlayPlacement.ResolveTopLeft((100, 200), PrimaryWorkArea, WindowWidth, WindowHeight);

        Assert.Equal((100, 200), (x, y));
    }

    [Fact]
    public void PullsASavedPositionBackInsideWhenTheMonitorItLivedOnIsGone()
    {
        // 上次存在副屏右边；这次副屏拔了 —— 不夹的话浮窗会落在 1920 宽的主屏之外
        var (x, y) = OverlayPlacement.ResolveTopLeft((3000, 1500), PrimaryWorkArea, WindowWidth, WindowHeight);

        Assert.Equal((1920 - WindowWidth, 1040 - WindowHeight), (x, y));
    }

    [Fact]
    public void CentersWithinAMonitorThatSitsLeftOfThePrimaryOne()
    {
        // 主显示器左边那块屏：X 是负的。这里集中体现"不要假设坐标 >= 0"
        var workArea = new OverlayRect(-1920, 0, 1920, 1040);

        var (x, y) = OverlayPlacement.ResolveTopLeft(null, workArea, WindowWidth, WindowHeight);

        Assert.Equal(-1920 + ((1920 - WindowWidth) / 2), x);
        Assert.Equal((1040 - WindowHeight) / 2, y);
    }

    [Fact]
    public void KeepsASavedPositionOnTheNegativeCoordinateMonitor()
    {
        var workArea = new OverlayRect(-1920, 0, 1920, 1040);

        // 上一次已经把它拖到 -1300：夹取不能因为"是负数"就把它挪回 0
        var (x, y) = OverlayPlacement.ResolveTopLeft((-1300, 100), workArea, WindowWidth, WindowHeight);

        Assert.Equal((-1300, 100), (x, y));
    }

    [Fact]
    public void ClampsASavedPositionLeftOfTheNegativeCoordinateMonitor()
    {
        // 比工作区左边界还靠左 → 贴住左边界
        var workArea = new OverlayRect(-1920, 0, 1920, 1040);

        var (x, _) = OverlayPlacement.ResolveTopLeft((-5000, 100), workArea, WindowWidth, WindowHeight);

        Assert.Equal(-1920, x);
    }

    [Fact]
    public void PinsToTheWorkAreaOriginWhenTheWindowIsLargerThanTheWorkArea()
    {
        // 窗口比工作区还大时上下限会颠倒 —— Math.Clamp 在这种情况下会**抛异常**，
        // 所以这条测试同时钉住"不许炸"
        var (x, y) = OverlayPlacement.ResolveTopLeft((500, 500), new OverlayRect(0, 0, 800, 600), 1200, 900);

        Assert.Equal((0, 0), (x, y));
    }

    [Fact]
    public void KeepsTheWindowFullyInsideTheWorkAreaAtItsBottomRightCorner()
    {
        var (x, y) = OverlayPlacement.ResolveTopLeft(
            (PrimaryWorkArea.Width, PrimaryWorkArea.Height), PrimaryWorkArea, WindowWidth, WindowHeight);

        // 存的是右下角（右下角恰好等于工作区右下角）→ 整个窗口仍要完整可见
        Assert.Equal((1920 - WindowWidth, 1040 - WindowHeight), (x, y));
    }
}