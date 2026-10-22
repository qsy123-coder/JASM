using GIMI_ModManager.Core.Helpers;

namespace JASM.Tests;

/// <summary>
/// Covers <see cref="OverlayCaptureRegion"/> — 拖框归一化与复用旧区域时的夹取。
///
/// 这两件事都不能靠肉眼验：用户可以从右下往左上拖，也可能在换过分辨率之后复用旧坐标。
/// 归一化错了截到的是反的矩形；夹取错了要么抛异常（负宽高），要么截出一张 3 像素宽的废图。
/// </summary>
public class OverlayCaptureRegionTests
{
    [Theory]
    [InlineData(100, 200, 300, 400)]
    [InlineData(300, 400, 100, 200)]
    [InlineData(300, 200, 100, 400)]
    [InlineData(100, 400, 300, 200)]
    public void FromPointsNormalizesAnyDragDirection(int startX, int startY, int endX, int endY)
    {
        var region = OverlayCaptureRegion.FromPoints(startX, startY, endX, endY);

        Assert.Equal(100, region.X);
        Assert.Equal(200, region.Y);
        Assert.Equal(200, region.Width);
        Assert.Equal(200, region.Height);
    }

    [Fact]
    public void MinimumSideIsEightPixels()
    {
        Assert.True(new OverlayCaptureRegion(0, 0, 8, 8).IsUsable);
        Assert.False(new OverlayCaptureRegion(0, 0, 7, 8).IsUsable);
        Assert.False(new OverlayCaptureRegion(0, 0, 8, 7).IsUsable);
        Assert.False(new OverlayCaptureRegion(0, 0, -8, 8).IsUsable);
    }

    [Fact]
    public void ClampKeepsARegionThatIsAlreadyInside()
    {
        var region = new OverlayCaptureRegion(100, 100, 200, 150);
        var bounds = new OverlayRect(0, 0, 1920, 1080);

        Assert.Equal(region, region.ClampTo(bounds));
    }

    [Fact]
    public void ClampShrinksThePartThatSticksOut()
    {
        var region = new OverlayCaptureRegion(1800, 1000, 400, 200);
        var bounds = new OverlayRect(0, 0, 1920, 1080);

        var clamped = region.ClampTo(bounds);

        Assert.Equal(new OverlayCaptureRegion(1800, 1000, 120, 80), clamped);
    }

    [Fact]
    public void ClampHandlesNegativeVirtualScreenCoordinates()
    {
        // 副屏摆在主屏左边时整块屏幕的 X 是负的
        var region = new OverlayCaptureRegion(-2000, 100, 500, 400);
        var bounds = new OverlayRect(-1920, 0, 1920, 1080);

        Assert.Equal(new OverlayCaptureRegion(-1920, 100, 420, 400), region.ClampTo(bounds));
    }

    [Fact]
    public void ClampReturnsNullWhenTheRegionIsCompletelyOutside()
    {
        var region = new OverlayCaptureRegion(3000, 100, 200, 200);

        Assert.Null(region.ClampTo(new OverlayRect(0, 0, 1920, 1080)));
    }

    [Fact]
    public void ClampReturnsNullWhenOnlyAnUnusableSliverSurvives()
    {
        var region = new OverlayCaptureRegion(1900, 100, 200, 200);

        // 夹完只剩 20×200 —— 够短的一边仍能用；再往外挪到只剩 3 像素就该判无效
        Assert.NotNull(region.ClampTo(new OverlayRect(0, 0, 1920, 1080)));
        Assert.Null(new OverlayCaptureRegion(1917, 100, 200, 200).ClampTo(new OverlayRect(0, 0, 1920, 1080)));
    }

    [Fact]
    public void ClampRejectsDegenerateBounds()
    {
        Assert.Null(new OverlayCaptureRegion(0, 0, 100, 100).ClampTo(new OverlayRect(0, 0, 0, 1080)));
        Assert.Null(new OverlayCaptureRegion(0, 0, 100, 100).ClampTo(new OverlayRect(0, 0, 1920, -1)));
    }
}
