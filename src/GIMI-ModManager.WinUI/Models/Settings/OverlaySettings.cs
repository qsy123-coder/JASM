using GIMI_ModManager.Core.Helpers;
using Newtonsoft.Json;

namespace GIMI_ModManager.WinUI.Models.Settings;

/// <summary>
/// 游戏内浮窗（Overlay）自己的设置。
///
/// 存 <c>SettingScope.App</c> 而不是 <c>Game</c>：窗口座标与「上次看的角色」是这台机器上这个人的习惯，
/// 换个游戏不该各存一份 —— 浮窗一次只服务一个游戏，但座标是屏幕座标，换游戏后照样成立。
///
/// 字段一律可空：文件缺失、字段被手改坏，都要退化成「从没设置过」而不是抛异常。
/// 读这个设置发生在浮窗**创建之前**，此刻抛异常等于浮窗永远打不开。
/// </summary>
public class OverlaySettings
{
    [JsonIgnore] public const string Key = "OverlaySettings";

    /// <summary>
    /// 浮窗左上角 X。**单位是物理像素**、虚拟屏幕坐标（副屏摆在主屏左边时 X 为负），
    /// 与 <c>AppWindow.Position</c> / <c>WindowEx.Width</c> 那套 DIP 差一个 DPI 缩放系数，别混用。
    /// <c>null</c> = 用户没拖过，浮窗居中。
    /// </summary>
    public int? XPosition { get; set; }

    /// <summary>浮窗左上角 Y。同 <see cref="XPosition"/>。</summary>
    public int? YPosition { get; set; }

    /// <summary>
    /// 上次在浮窗里选中的角色，存 <c>InternalName</c>。
    ///
    /// 用 <c>InternalName</c> 而不是显示名：显示名随语言变、也可能被用户改写，
    /// 拿它当键会在换语言之后失配。
    /// </summary>
    public string? LastSelectedCharacter { get; set; }

    /// <summary>
    /// <c>false</c>（默认）= <b>单选</b>：勾一下立刻生效（自动 F10），且同角色只留一件 ——
    /// 勾上新的会把其它已勾的收起来。<c>true</c> = <b>多选</b>：自由勾选，攒够了点浮窗上的「刷新」。
    ///
    /// 用 bool 而不是 0/1 序号之类的魔数：设置文件是给人看的，序号还得配一张表才读得懂。
    /// 老设置文件里没有这个字段 → 反序列化成 <c>false</c>，正是我们要的默认值（单选），不需要额外迁移。
    /// </summary>
    public bool MultiSelectMode { get; set; }

    /// <summary>
    /// 两个座标都存过才算数。只存下一个（比如文件被手改坏）时宁可当没存过、重新居中，
    /// 也不要拿半份座标去摆窗口 —— 那会把浮窗摆到一个没人预期的地方。
    /// </summary>
    [JsonIgnore] public bool HasSavedPosition => XPosition.HasValue && YPosition.HasValue;

    /// <summary>喂给 <c>OverlayPlacement.ResolveTopLeft</c> 的入参；没存过时为 <c>null</c>。</summary>
    [JsonIgnore]
    public (int X, int Y)? SavedTopLeft =>
        HasSavedPosition ? (XPosition!.Value, YPosition!.Value) : null;

    /// <summary>
    /// 浮窗尺寸档位：0 = 小、1 = 中、2 = 大（见 <c>OverlayViewModel.SizeOptions</c>）。
    /// <c>null</c> = 没存过 → 中档。
    /// </summary>
    public int? SizePreset { get; set; }

    /// <summary>「修复」页：补全贴图状态。与 <see cref="FixStableTexture"/> 互斥，默认关。</summary>
    public bool FixDerivedHashes { get; set; }

    /// <summary>「修复」页：应用稳定纹理。与 <see cref="FixDerivedHashes"/> 互斥，默认关。</summary>
    public bool FixStableTexture { get; set; }

    /// <summary>「修复」页：模组破洞/部位缺失修复（Rendering 3.3）。默认关。</summary>
    public bool FixRendering33 { get; set; }

    /// <summary>「修复」页：修复爱弥斯机兵形态的模型异常。默认关。</summary>
    public bool FixAemeathMech { get; set; }

    /// <summary>「修复」页：女漂-风主眼部修复模式，0 = 关、1 = TexCoord 覆盖、2 = 贴图镜像反转。</summary>
    public int? FixAeroMode { get; set; }

    /// <summary>
    /// 鸣潮资源档位：0 = 自动、1 = SD、2 = HD、3 = UHD。
    /// <c>null</c> = 没设置过 → 自动。用于启动参数与 UHD 修复提示；换游戏不该共用，
    /// 但浮窗一次只服务一个游戏，且这个字段只在鸣潮页面显示。
    /// </summary>
    public int? UhdResourceTierIndex { get; set; }

    /// <summary>
    /// UHD 修复方式：0 = 基础哈希、1 = 稳定纹理、2 = 补全状态贴图（RabbitFX）。
    /// <c>null</c> = 没设置过 → 基础哈希。普通 mod 不应默认走 RabbitFX 派生哈希。
    /// </summary>
    public int? UhdFixModeIndex { get; set; }

    /// <summary>「修复」页的二级视图：0 = 修复、1 = 回滚。<c>null</c> = 修复。</summary>
    public int? FixSubTab { get; set; }

    /// <summary>
    /// 是否已经做过「首次进入修复页自动选大档」。
    /// 只做一次：之后用户手动选小/中，再进修复页不能被再次覆盖。
    /// </summary>
    public bool FixSizeInitialized { get; set; }

    /// <summary>日志区是否展开。<c>null</c>/false = 折叠。</summary>
    public bool FixLogExpanded { get; set; }

    /// <summary>
    /// 上次静默检查配置更新的 UTC 时间（ISO 8601）。用于 12 小时节流；
    /// 解析失败当作没查过，不影响修复功能。
    /// </summary>
    public string? LastConfigCheckUtc { get; set; }

    /// <summary>
    /// 用户自定义的浮窗唤出热键：修饰键（<c>HotkeyModifiers</c> 的位组合）。
    /// 三项任一为空 = 没自定义过，沿用默认候选表。
    /// </summary>
    public int? OverlayHotkeyModifiers { get; set; }

    /// <summary>自定义热键的主键虚拟键码。</summary>
    public int? OverlayHotkeyVirtualKey { get; set; }

    /// <summary>自定义热键主键的显示名（<c>J</c> / <c>F9</c> / <c>Space</c>）。</summary>
    public string? OverlayHotkeyKeyName { get; set; }

    /// <summary>
    /// 截图输出目录。<c>null</c> = 没配过，用
    /// <c>OverlayScreenshotService.GetDefaultOutputFolder()</c>（图片\JASM\浮窗截图）。
    /// </summary>
    public string? ScreenshotOutputFolder { get; set; }

    /// <summary>最后一次成功截图用的区域（虚拟屏幕物理像素）。复用区域就靠这四个值。</summary>
    public int? CaptureRegionX { get; set; }

    public int? CaptureRegionY { get; set; }

    public int? CaptureRegionWidth { get; set; }

    public int? CaptureRegionHeight { get; set; }

    /// <summary>
    /// 四项都存过且宽高够用才算数。只存下一半时宁可当没存过、让用户重新框选 ——
    /// 拿半份坐标去截图只会截到一块没人预期的地方。
    /// </summary>
    [JsonIgnore]
    public bool HasSavedCaptureRegion =>
        CaptureRegionX.HasValue && CaptureRegionY.HasValue
        && CaptureRegionWidth.HasValue && CaptureRegionHeight.HasValue
        && CaptureRegionWidth.Value >= OverlayCaptureRegion.MinimumSide
        && CaptureRegionHeight.Value >= OverlayCaptureRegion.MinimumSide;

    /// <summary>喂给截图流程的入参；没存过时为 <c>null</c>。</summary>
    [JsonIgnore]
    public OverlayCaptureRegion? SavedCaptureRegion =>
        HasSavedCaptureRegion
            ? new OverlayCaptureRegion(CaptureRegionX!.Value, CaptureRegionY!.Value,
                CaptureRegionWidth!.Value, CaptureRegionHeight!.Value)
            : null;

    /// <summary>记住这次成功截图用的区域。</summary>
    public void RememberCaptureRegion(OverlayCaptureRegion region)
    {
        CaptureRegionX = region.X;
        CaptureRegionY = region.Y;
        CaptureRegionWidth = region.Width;
        CaptureRegionHeight = region.Height;
    }

    /// <summary>清除记忆区域（换分辨率 / 想重新框一个构图时用）。</summary>
    public void ClearCaptureRegion()
    {
        CaptureRegionX = null;
        CaptureRegionY = null;
        CaptureRegionWidth = null;
        CaptureRegionHeight = null;
    }
}