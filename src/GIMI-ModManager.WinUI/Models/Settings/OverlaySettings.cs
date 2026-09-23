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
    /// 两个座标都存过才算数。只存下一个（比如文件被手改坏）时宁可当没存过、重新居中，
    /// 也不要拿半份座标去摆窗口 —— 那会把浮窗摆到一个没人预期的地方。
    /// </summary>
    [JsonIgnore] public bool HasSavedPosition => XPosition.HasValue && YPosition.HasValue;

    /// <summary>喂给 <c>OverlayPlacement.ResolveTopLeft</c> 的入参；没存过时为 <c>null</c>。</summary>
    [JsonIgnore] public (int X, int Y)? SavedTopLeft =>
        HasSavedPosition ? (XPosition!.Value, YPosition!.Value) : null;
}