using GIMI_ModManager.Core.Helpers;

namespace JASM.Tests;

/// <summary>
/// Covers <see cref="CursorRecenterGuard"/> — 送完键之后要不要把光标放回原处。
///
/// 两个方向都要钉：漏判 → 光标留在屏幕中央（用户得自己挪回去，浮窗的自愈也跟着失效）；
/// 误判 → 把用户正在移动的光标拽回去（"鼠标被拉扯"，同一批用户抱怨过的那种）。
/// 所以「用户自己挪走」的几种走法必须逐个断言为 false。
/// </summary>
public class CursorRecenterGuardTests
{
    /// <summary>全屏游戏：窗口铺满 2560x1600，中心就是屏幕中心（GitHub 上那台实机就是这个分辨率）。</summary>
    private const int GameCenterX = 1280;
    private const int GameCenterY = 800;

    [Fact]
    public void ACursorThatDidNotMoveIsNeverRestored()
    {
        // 位置没变 → 不还原。哪怕它正好在中心（用户自己把鼠标停在那儿、或者上一次已经挪好）也不动
        Assert.False(CursorRecenterGuard.IsRecenteredToGameCenter(400, 300, 400, 300, GameCenterX, GameCenterY));
        Assert.False(CursorRecenterGuard.IsRecenteredToGameCenter(
            GameCenterX, GameCenterY, GameCenterX, GameCenterY, GameCenterX, GameCenterY));
    }

    [Fact]
    public void ACursorSittingOnTheGameCenterIsRestored()
    {
        // 送的这一键之前光标在浮窗上（400,300），送完躺在游戏窗口正中心 —— 这就是要还原的那一下
        Assert.True(CursorRecenterGuard.IsRecenteredToGameCenter(400, 300, GameCenterX, GameCenterY,
            GameCenterX, GameCenterY));
    }

    [Fact]
    public void TheToleranceCoversASlightlyOffCenterCursor()
    {
        var edge = CursorRecenterGuard.TolerancePixels;

        Assert.True(CursorRecenterGuard.IsRecenteredToGameCenter(400, 300, GameCenterX + edge, GameCenterY - edge,
            GameCenterX, GameCenterY));
    }

    [Fact]
    public void JustOutsideTheToleranceIsTreatedAsTheUsersOwnMove()
    {
        var beyond = CursorRecenterGuard.TolerancePixels + 1;

        // 只差一个像素就放过：宁可漏判（毛病照旧，日志里有坐标可调）也不乱动用户的光标
        Assert.False(CursorRecenterGuard.IsRecenteredToGameCenter(400, 300, GameCenterX + beyond, GameCenterY,
            GameCenterX, GameCenterY));
        Assert.False(CursorRecenterGuard.IsRecenteredToGameCenter(400, 300, GameCenterX, GameCenterY + beyond,
            GameCenterX, GameCenterY));
    }

    [Fact]
    public void ACursorTheUserMovedElsewhereIsLeftAlone()
    {
        // 用户在送键那一秒里自己把光标挪到浮窗另一头：还原会把刚挪的位置拽回去，绝不能做
        Assert.False(CursorRecenterGuard.IsRecenteredToGameCenter(400, 300, 620, 540, GameCenterX, GameCenterY));
    }

    [Fact]
    public void ACursorMovedAwayFromTheCenterIsLeftAlone()
    {
        // 反方向：本来在中心（上一次没还原成），这次用户自己挪走了 —— 也别再往回拽
        Assert.False(CursorRecenterGuard.IsRecenteredToGameCenter(GameCenterX, GameCenterY, 400, 300,
            GameCenterX, GameCenterY));
    }

    [Fact]
    public void AWindowedGameUsesItsOwnCenterNotTheScreenCenter()
    {
        // 窗口模式：游戏窗口在屏幕右下角，它自己的中心离屏幕中心很远 —— 判据必须按窗口中心走
        const int windowCenterX = 1900;
        const int windowCenterY = 1200;

        Assert.True(CursorRecenterGuard.IsRecenteredToGameCenter(400, 300, windowCenterX, windowCenterY,
            windowCenterX, windowCenterY));

        // 屏幕中心在这时反而成了"别处"，不该被认成游戏把光标收走了
        Assert.False(CursorRecenterGuard.IsRecenteredToGameCenter(400, 300, GameCenterX, GameCenterY,
            windowCenterX, windowCenterY));
    }
}