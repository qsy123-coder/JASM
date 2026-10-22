namespace GIMI_ModManager.WinUI.ViewModels.Overlay;

/// <summary>
/// 浮窗的一档预设尺寸。**单位是 DIP**（<c>WindowEx.Width/Height</c> 的语义），
/// 与 <c>AppWindow.Size</c> 的物理像素差一个 DPI 缩放系数，别混用。
///
/// 三档由产品定死（PRD v1.1）：小 320×300 / 中 380×360 / 大 460×480。
/// 标题栏的下拉框直接绑 <see cref="DisplayName"/>。
/// </summary>
public sealed record OverlaySizeOption(string DisplayName, int Width, int Height);
