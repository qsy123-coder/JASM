using System.Globalization;
using System.Text;

namespace GIMI_ModManager.Core.Entities.Mods.InGameMenu;

/// <summary>生成菜单时的可配置项。</summary>
public sealed record InGameMenuOptions
{
    /// <summary>
    /// 3DMigoto <c>[Key*]</c> 里写的呼出键，如 <c>F7</c>。
    /// 默认不能用 F8：d3dx.ini 把 F8 绑给 frame analysis（<c>analyse_frame</c>），
    /// 会与菜单开关抢键并逐帧重复触发。
    /// </summary>
    public string OpenKeyTokens { get; init; } = "F7";

    /// <summary>UI 上显示的同一个键（用于底部提示）。</summary>
    public string OpenKeyDisplay { get; init; } = "F7";

    /// <summary>菜单标题（通常是 Mod 显示名，会自动拆成两行）。</summary>
    public string Title { get; init; } = "JASM";

    /// <summary>一页最多几行。</summary>
    public int MaxRowsPerPage { get; init; } = 10;

    /// <summary>Mod 是否提供 <c>$object_detected</c>；没有就不加门控。</summary>
    public bool GateOnObjectDetected { get; init; } = true;

    /// <summary>
    /// 角色模型的纹理 hash（来自 Mod 里设置 <c>$object_detected = 1</c> 的 TextureOverride）。
    /// <see cref="DetectionVariable"/> 为空时才作为后备门控。
    /// </summary>
    public string DetectionHash { get; init; } = string.Empty;

    /// <summary>
    /// 逐角色门控变量（完整限定名，如 <c>$\JasmXXXXXXXX\object_detected</c>）。
    ///
    /// 补了 ASCII namespace 之后，每个 Mod 的 <c>$object_detected</c> 都是独立的：
    /// Mod 的组件绘制时置 1，自己的 <c>[Present]</c> 再 post 回 0。
    /// 用它做门控比 hash 更准，而且呼出键只切换当前角色那份菜单。
    /// </summary>
    public string DetectionVariable { get; init; } = string.Empty;

    /// <summary>
    /// 生成器版本，写进 ini 头与 manifest，便于日后失效重生成。
    ///
    /// v3：命中检测改用 <c>cursor_screen_x/y</c>。v2 用 <c>cursor_x/y</c>，
    /// 在游戏隐藏光标时会冻结，导致菜单可见但鼠标点击永远命中不了。
    /// v4：没有 namespace 的 Mod 自动补 ASCII namespace；v3 用含中文的 ini 路径当限定名，
    /// 3DMigoto 解析器会报语法错误，点击不会作用到 Mod。
    /// v5：逐角色门控改用 Mod 自己的限定 <c>object_detected</c>；hash 可能命中共享资源导致多份菜单叠在一起。
    /// v6：门控不再加在呼出键上（按键处理时 object_detected 已被 post 回 0，呼出键关不掉），
    ///     改为角色不在场时在 Present 里自动 <c>open = 0</c>。
    /// v7：默认呼出键 F8 → F6（F8 被 d3dx.ini 绑给 frame analysis）。
    /// v8：F6 被 WWMI 核心占用（toggle mods），默认改为 F7。
    /// v9：呼出键从 type=cycle 改为 hold + 自行检测按下沿，避免与自动归零 resync 导致第一次按无效。
    /// v10：hold 边沿在低帧率下会漏（按下/抬起同帧）；改用 cycle 脉冲变量 + Present 检测变化。
    /// v11：修复门控 if 缺少 endif（绘制变成无条件执行，F7 改 open 但菜单不消失）。
    /// v12：竖栏增加 ↺「恢复默认」，把所有行写回 Mod ini 声明的初始值。
    /// v13：键盘导航（↑/↓ 选择、Enter 切换）与选中行高亮。
    /// v14：移除键盘导航（会覆盖分页页码），恢复纯鼠标交互。
    /// v15：新增「深灰米色（参考样式）」主题并设为默认（参考《心·绀星绮愿》GUI）。
    /// v16：新增整块深色背板，贴近参考菜单的深灰底。
    /// v17：新增「参考素材」主题，直接拷贝参考 Mod 的按钮/页签 PNG。
    /// v18：参考素材样式改用原生素材尺寸、无背板/无下划线、每行米色描边。
    /// v19：整体缩小。
    /// v20：按全屏截图比例调整尺寸。
    /// v21：行名称接中英词表；值改为「序号/总数」并右对齐。
    /// v22：补入心月狐 100+ mod 扫描出的常用标签翻译。
    /// v23：设置作用域修正后强制重建，确保所有菜单用上参考素材主题。
    /// </summary>
    public const int CurrentGeneratorVersion = 23;

    /// <summary>生成器版本，写进 ini 头与 manifest，便于日后失效重生成。</summary>
    public int GeneratorVersion { get; init; } = CurrentGeneratorVersion;

    /// <summary>诊断用：在光标计算位置画一个红色小方块。</summary>
    public bool DebugCursor { get; init; }

    /// <summary>菜单行的显示顺序（<see cref="ModIniToggleRow.Key"/> 列表）；未列出的行按原顺序追加。</summary>
    public IReadOnlyList<string> RowOrder { get; init; } = [];

    /// <summary>要隐藏的行（<see cref="ModIniToggleRow.Key"/> 列表）。</summary>
    public IReadOnlyCollection<string> HiddenRows { get; init; } = [];

    /// <summary>菜单皮肤；换皮肤后需要重建菜单。</summary>
    public MenuTheme Theme { get; init; } = MenuThemes.Reference;

    /// <summary>参考素材目录（参考 Mod 的 GUI\res），空表示不使用 PNG 素材。</summary>
    public string ReferenceAssetsDirectory { get; init; } = string.Empty;

    /// <summary>哪些资源改用 .png 源文件（Row / RowHover / Tab / TabActive）。</summary>
    public IReadOnlyCollection<string> PngResources { get; init; } = [];
}

/// <summary>一张待生成的纹理。文字类由生成器渲染，面板类由 <see cref="MenuTextureFactory"/> 绘制。</summary>
public sealed record MenuTextureSpec(
    string FileName,
    MenuTextureKind Kind,
    string Text,
    int Width,
    int Height,
    int FontSize,
    bool Bold,
    GdiTextRasterizer.TextAlign Align);

public enum MenuTextureKind
{
    /// <summary>旧的整块面板（保留以兼容旧调用）。</summary>
    Panel,

    /// <summary>旧的悬停块（保留以兼容旧调用）。</summary>
    Hover,
    Text,
    Marker,

    /// <summary>单行底条（常态）。</summary>
    RowBar,

    /// <summary>单行底条（悬停）。</summary>
    RowHover,

    /// <summary>顶部页签（常态）。</summary>
    Tab,

    /// <summary>顶部页签（选中）。</summary>
    TabActive,

    /// <summary>左侧竖栏。</summary>
    Sidebar,

    /// <summary>标题下划线。</summary>
    Underline,
}

/// <summary><see cref="InGameMenuIniBuilder.Build"/> 的产物：ini 文本 + 需要落盘的纹理清单。</summary>
public sealed record InGameMenuBuildResult(
    string IniText,
    IReadOnlyList<MenuTextureSpec> Textures,
    int PanelWidth,
    int PanelHeight,
    int PageCount);

/// <summary>
/// 把解析出来的行拼成 3DMigoto 能加载的 <c>JASM_Menu.ini</c>，布局仿照《清宵·绯花令》菜单：
///
/// • 左上两行标题 + 青色下划线；
/// • 顶部页签（当前页高亮）；
/// • 左侧竖栏（页码 + 关闭）；
/// • 每行：左箭头、名称、当前值、右侧按键提示、右箭头；
/// • 底部三行说明文字。
///
/// 交互：<c>F7</c> 开关；按住 Alt 后左键点击行/页签/页码，右键拖动菜单。
/// 所有坐标写在 1920×1080 虚拟空间，运行时按 <c>min(res_w/1920, res_h/1080)</c> 缩放。
/// </summary>
public static class InGameMenuIniBuilder
{
    // —— 布局（虚拟坐标）——
    private const int AnchorDefaultX = 360;
    private const int AnchorDefaultY = 120;

    private const int TitleWidth = 360;
    private const int TitleHeight = 50;
    private const int TitleLineGap = 4;
    private const int UnderlineOffsetY = 106;
    private const int UnderlineWidth = 220;
    private const int UnderlineHeight = 3;

    private const int TabsOffsetX = 10;
    private const int TabsOffsetY = 118;
    private const int TabWidth = 160;
    private const int TabHeight = 38;
    private const int TabGap = 10;

    private const int SidebarOffsetX = -58;
    private const int SidebarOffsetY = 118;
    private const int SidebarWidth = 44;
    private const int SidebarNumberHeight = 40;
    private const int CloseOffsetY = 56;
    private const int CloseWidth = 44;
    private const int CloseHeight = 40;

    private const int RowOffsetX = 20;
    private const int RowOffsetY = 168;
    private const int RowWidth = 620;

    // 整块深色背板（参考菜单的深灰底），覆盖标题/竖栏/行/底部说明
    private const int PanelTextureWidth = 720;
    private const int PanelOffsetX = -68;
    private const int PanelOffsetY = -10;
    private const int PanelExtraHeight = 12;
    private const int RowHeight = 64;
    private const int RowGap = 6;

    private const int ArrowLeftOffsetX = 32;
    private const int ArrowRightOffsetX = 596;
    private const int ArrowOffsetY = 18;
    private const int ArrowWidth = 24;
    private const int ArrowHeight = 28;

    private const int LabelOffsetX = 64;
    private const int LabelOffsetY = 6;
    private const int LabelWidth = 360;
    private const int LabelHeight = 28;

    private const int ValueOffsetY = 34;
    private const int ValueHeight = 24;

    private const int HintOffsetX = 470;
    private const int HintOffsetY = 20;
    private const int HintWidth = 110;
    private const int HintHeight = 24;

    private const int BottomHintOffsetX = 90;
    private const int BottomHintWidth = 520;
    private const int BottomHintHeight = 22;
    private const int BottomHintLineGap = 26;

    public const int DefaultRowsPerPage = 10;

    // 参考素材样式：按钮素材原生 720×108，每页 7 行才能放进 1080 高度
    private const int ReferenceRowWidth = 430;
    private const int ReferenceRowHeight = 56;
    private const int ReferenceRowsPerPage = 11;

    private static int RowsPerPage(InGameMenuOptions options)
        => Math.Clamp(options.MaxRowsPerPage, 1,
            options.Theme.UseReferenceAssets ? ReferenceRowsPerPage : 20);

    private static int RowWidthFor(InGameMenuOptions options)
        => options.Theme.UseReferenceAssets ? ReferenceRowWidth : RowWidth;

    private static int RowHeightFor(InGameMenuOptions options)
        => options.Theme.UseReferenceAssets ? ReferenceRowHeight : RowHeight;

    private static int RowOffsetYFor(InGameMenuOptions options)
        => options.Theme.UseReferenceAssets ? 116 : RowOffsetY;

    private static int TabWidthFor(InGameMenuOptions options)
        => options.Theme.UseReferenceAssets ? 180 : TabWidth;

    private static int TabHeightFor(InGameMenuOptions options)
        => options.Theme.UseReferenceAssets ? 30 : TabHeight;

    private static int LabelWidthFor(InGameMenuOptions options)
        => options.Theme.UseReferenceAssets ? 200 : LabelWidth;

    private static int TitleWidthFor(InGameMenuOptions options)
        => options.Theme.UseReferenceAssets ? 300 : TitleWidth;

    private static int TitleHeightFor(InGameMenuOptions options)
        => options.Theme.UseReferenceAssets ? 34 : TitleHeight;

    private static int TitleFontFor(InGameMenuOptions options)
        => options.Theme.UseReferenceAssets ? 24 : 32;

    private static int TitleLineGapFor(InGameMenuOptions options)
        => options.Theme.UseReferenceAssets ? 2 : TitleLineGap;

    private static int TabsOffsetYFor(InGameMenuOptions options)
        => options.Theme.UseReferenceAssets ? 78 : TabsOffsetY;

    private static int HintOffsetYFor(InGameMenuOptions options)
        => options.Theme.UseReferenceAssets ? 4 : HintOffsetY;

    public static InGameMenuBuildResult Build(
        IReadOnlyList<ModIniToggleRow> rows,
        string menuToken,
        InGameMenuOptions options)
    {
        var token = NormalizeToken(menuToken);
        var v = "$jasm_" + token + "_"; // 自有变量前缀

        var rowsPerPage = RowsPerPage(options);
        options = options with { MaxRowsPerPage = rowsPerPage };

        var rowCount = rows.Count;
        var pageCount = Math.Max(1, (rowCount + rowsPerPage - 1) / rowsPerPage);
        var visibleRows = Math.Min(rowsPerPage, Math.Max(1, rowCount));
        var menuHeight = RowOffsetYFor(options) + visibleRows * (RowHeightFor(options) + RowGap) + 3 * BottomHintLineGap + 16;

        var (titleLine1, titleLine2) = SplitTitle(options.Title);
        var textures = new List<MenuTextureSpec>();

        // —— 整块深色背板 ——
        // 参考素材样式没有实心背板（截图里是游戏画面直接透出），其余主题保留。
        if (!options.Theme.UseReferenceAssets)
        {
            textures.Add(new MenuTextureSpec("JASM_Menu/panel.dds", MenuTextureKind.Panel, string.Empty,
                PanelTextureWidth, menuHeight + PanelExtraHeight, 0, false, GdiTextRasterizer.TextAlign.Left));
        }

        // —— 顶部：标题 + 下划线 ——
        textures.Add(Text("JASM_Menu/title_1.dds", titleLine1, TitleWidthFor(options), TitleHeightFor(options), TitleFontFor(options), true,
            GdiTextRasterizer.TextAlign.Left));
        textures.Add(Text("JASM_Menu/title_2.dds", titleLine2, TitleWidthFor(options), TitleHeightFor(options), TitleFontFor(options), true,
            GdiTextRasterizer.TextAlign.Left));
        textures.Add(new MenuTextureSpec("JASM_Menu/underline.dds", MenuTextureKind.Underline, string.Empty,
            UnderlineWidth, UnderlineHeight, 0, false, GdiTextRasterizer.TextAlign.Left));

        // —— 顶部页签 + 左侧页码 ——
        textures.Add(new MenuTextureSpec("JASM_Menu/tab.dds", MenuTextureKind.Tab, string.Empty,
            TabWidthFor(options), TabHeightFor(options), 0, false, GdiTextRasterizer.TextAlign.Left));
        textures.Add(new MenuTextureSpec("JASM_Menu/tab_active.dds", MenuTextureKind.TabActive, string.Empty,
            TabWidthFor(options), TabHeightFor(options), 0, false, GdiTextRasterizer.TextAlign.Left));
        textures.Add(new MenuTextureSpec("JASM_Menu/sidebar.dds", MenuTextureKind.Sidebar, string.Empty,
            SidebarWidth, pageCount * SidebarNumberHeight + 12, 0, false, GdiTextRasterizer.TextAlign.Left));
        textures.Add(Text("JASM_Menu/close.dds", "×", CloseWidth, CloseHeight, 24, false,
            GdiTextRasterizer.TextAlign.Center));
        textures.Add(Text("JASM_Menu/reset.dds", "↺", CloseWidth, CloseHeight, 24, false,
            GdiTextRasterizer.TextAlign.Center));

        for (var p = 0; p < pageCount; p++)
        {
            textures.Add(Text($"JASM_Menu/tab_label_{p:00}.dds", (p + 1).ToString(CultureInfo.InvariantCulture),
                TabWidthFor(options) - 10, TabHeightFor(options) - 4, 20, true,
                GdiTextRasterizer.TextAlign.Center));
            textures.Add(Text($"JASM_Menu/page_{p:00}.dds", (p + 1).ToString(CultureInfo.InvariantCulture),
                32, 32, 20, false, GdiTextRasterizer.TextAlign.Center));
        }

        // —— 行底条 + 箭头 ——
        textures.Add(new MenuTextureSpec("JASM_Menu/row.dds", MenuTextureKind.RowBar, string.Empty,
            RowWidth, RowHeight, 0, false, GdiTextRasterizer.TextAlign.Left));
        textures.Add(new MenuTextureSpec("JASM_Menu/row_hover.dds", MenuTextureKind.RowHover, string.Empty,
            RowWidth, RowHeight, 0, false, GdiTextRasterizer.TextAlign.Left));
        textures.Add(Text("JASM_Menu/arrow_left.dds", "‹", ArrowWidth, ArrowHeight, 26, false,
            GdiTextRasterizer.TextAlign.Center));
        textures.Add(Text("JASM_Menu/arrow_right.dds", "›", ArrowWidth, ArrowHeight, 26, false,
            GdiTextRasterizer.TextAlign.Center));

        // —— 底部说明 ——
        textures.Add(Text("JASM_Menu/hint_1.dds", "按住 Alt：左键点击切换 · 右键拖动菜单",
            BottomHintWidth, BottomHintHeight, 16, false, GdiTextRasterizer.TextAlign.Center));
        textures.Add(Text("JASM_Menu/hint_2.dds", $"{options.OpenKeyDisplay} 打开 / 关闭菜单",
            BottomHintWidth, BottomHintHeight, 16, false, GdiTextRasterizer.TextAlign.Center));
        textures.Add(Text("JASM_Menu/hint_3.dds", "左侧 ↺ 恢复默认 · JASM",
            BottomHintWidth, BottomHintHeight, 16, false, GdiTextRasterizer.TextAlign.Center));

        // —— 每行文字 ——
        for (var i = 0; i < rowCount; i++)
        {
            textures.Add(Text($"JASM_Menu/label_{i:00}.dds", rows[i].Label, LabelWidthFor(options), LabelHeight, 22, true,
                GdiTextRasterizer.TextAlign.Left));

            if (!string.IsNullOrWhiteSpace(rows[i].KeyDisplay))
            {
                textures.Add(Text($"JASM_Menu/hint_{i:00}.dds", rows[i].KeyDisplay, HintWidth, HintHeight, 16,
                    false, GdiTextRasterizer.TextAlign.Right));
            }

            for (var valueIndex = 0; valueIndex < rows[i].Values.Count; valueIndex++)
            {
                // 显示「第几个/共几个」（1 起算），而不是原始值 0/1/2
                var valueText = $"{valueIndex + 1}/{rows[i].Values.Count}";
                var valueAlign = options.Theme.UseReferenceAssets
                    ? GdiTextRasterizer.TextAlign.Right
                    : GdiTextRasterizer.TextAlign.Left;

                textures.Add(Text($"JASM_Menu/value_{i:00}_{valueIndex:00}.dds", valueText,
                    LabelWidthFor(options), ValueHeight, 17, false, valueAlign));
            }
        }

        if (options.DebugCursor)
        {
            textures.Add(new MenuTextureSpec("JASM_Menu/cursor.dds", MenuTextureKind.Marker, string.Empty,
                16, 16, 0, false, GdiTextRasterizer.TextAlign.Left));
        }

        var ini = new StringBuilder(96 * 1024);

        ini.AppendLine("; ============================================================");
        ini.AppendLine("; JASM 游戏内切换菜单（自动生成，请勿手改）");
        ini.AppendLine("; 删除本文件与 JASM_Menu 目录即可完全移除；作者原有 ini 未被修改");
        ini.AppendLine($"; generator = {options.GeneratorVersion}   rows = {rowCount}   pages = {pageCount}");
        ini.AppendLine("; ============================================================");
        ini.AppendLine();

        // 必须给 ASCII namespace：没有 namespace 时 3DMigoto 用 ini 路径当变量命名空间，
        // 而 Mod 路径常含中文，变量名会被判非法。
        ini.AppendLine($"namespace = {MenuNamespace(token)}");
        ini.AppendLine();

        // 呼出键：cycle 只翻转 key_pulse（不改 open），Present 检测脉冲变化再翻 open。
        // 不能用 hold 的按下沿：加载/低帧率时按下与抬起可能发生在同一帧，边沿会漏。
        ini.AppendLine($"[{KeyToggleSection(token)}]");
        ini.AppendLine($"key = {options.OpenKeyTokens}");
        ini.AppendLine("type = cycle");
        ini.AppendLine($"{v}key_pulse = 0,1");
        ini.AppendLine();

        // Alt+左键点击 / Alt+右键拖动（鸣潮按 Alt 时光标解锁）
        ini.AppendLine($"[{KeyMouseClickSection(token)}]");
        ini.AppendLine($"condition = {v}open == 1");
        ini.AppendLine("key = ALT VK_LBUTTON");
        ini.AppendLine("type = hold");
        ini.AppendLine($"{v}click_guard = 1");
        ini.AppendLine();
        ini.AppendLine($"[{KeyMouseDragSection(token)}]");
        ini.AppendLine($"condition = {v}open == 1");
        ini.AppendLine("key = ALT VK_RBUTTON");
        ini.AppendLine("type = hold");
        ini.AppendLine($"{v}drag_guard = 1");
        ini.AppendLine();

        AppendConstants(ini, v);
        AppendDetector(ini, v, token, options);
        AppendPresent(ini, v, token, options, pageCount, menuHeight);
        AppendChrome(ini, v, token, pageCount, visibleRows, rows, menuHeight, options);

        for (var p = 0; p < pageCount; p++)
            AppendPageCommandList(ini, token, p, rowCount, rowsPerPage);

        for (var i = 0; i < rowCount; i++)
            AppendRow(ini, v, token, i, rows[i], options);

        AppendResources(ini, token, rows, pageCount, options);
        AppendShader(ini, v, token);

        return new InGameMenuBuildResult(ini.ToString(), textures, RowWidthFor(options), menuHeight, pageCount);
    }

    private static MenuTextureSpec Text(string file, string text, int w, int h, int font, bool bold,
        GdiTextRasterizer.TextAlign align) =>
        new(file, MenuTextureKind.Text, text, w, h, font, bold, align);

    private static void AppendConstants(StringBuilder ini, string v)
    {
        ini.AppendLine("[Constants]");
        // open/page 不 persist：菜单每次启动都是关闭的；只有位置 x/y 需要记住。
        ini.AppendLine($"global {v}open = 0");
        ini.AppendLine($"global persist {v}x = {AnchorDefaultX}");
        ini.AppendLine($"global persist {v}y = {AnchorDefaultY}");
        ini.AppendLine($"global {v}page = 0");
        ini.AppendLine($"global {v}click_guard = 0");
        ini.AppendLine($"global {v}key_pulse = 0");
        ini.AppendLine($"global {v}key_pulse_prev = 0");
        ini.AppendLine($"global {v}prev_down = 0");
        ini.AppendLine($"global {v}click = 0");
        ini.AppendLine($"global {v}drag_guard = 0");
        ini.AppendLine($"global {v}dragging = 0");
        ini.AppendLine($"global {v}drag_mx = 0");
        ini.AppendLine($"global {v}drag_my = 0");
        ini.AppendLine($"global {v}drag_x = 0");
        ini.AppendLine($"global {v}drag_y = 0");
        ini.AppendLine($"global {v}mx = 0");
        ini.AppendLine($"global {v}my = 0");
        ini.AppendLine($"global {v}scale = 1");
        ini.AppendLine($"global {v}in_rect = 0");
        ini.AppendLine($"global {v}img_w = 0");
        ini.AppendLine($"global {v}img_h = 0");
        ini.AppendLine($"global {v}img_x = 0");
        ini.AppendLine($"global {v}img_y = 0");
        ini.AppendLine($"global {v}tint_a = 1");
        ini.AppendLine($"global {v}first_frame = 1");
        ini.AppendLine($"global {v}last_seen = -1000");
        ini.AppendLine($"global {v}age = 0");
        ini.AppendLine();
    }

    /// <summary>
    /// 逐角色检测：用 Mod 自己设置 <c>$object_detected = 1</c> 时的纹理 hash，
    /// 只有该角色的模型在绘制时才会刷新 <c>last_seen</c>。
    /// </summary>
    private static void AppendDetector(StringBuilder ini, string v, string token, InGameMenuOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.DetectionHash))
            return;

        ini.AppendLine($"[{DetectorSection(token)}]");
        ini.AppendLine($"hash = {options.DetectionHash}");
        ini.AppendLine($"{v}last_seen = time");
        ini.AppendLine();
    }

    private static void AppendPresent(StringBuilder ini, string v, string token, InGameMenuOptions options,
        int pageCount, int menuHeight)
    {
        // [Present] 只做无条件转发：把 if 门控放进 CommandList 里。
        // 实机验证：非命名空间的 mod ini 里直接写在 [Present] 的 if 不会生效。
        ini.AppendLine("[Present]");
        ini.AppendLine($"run = {EntryCommandList(token)}");
        ini.AppendLine();
        ini.AppendLine($"[{EntryCommandList(token)}]");
        ini.AppendLine("; 首帧把菜单状态压回关闭（F10 重载后也成立）");
        ini.AppendLine($"if {v}first_frame == 1");
        ini.AppendLine($"    {v}open = 0");
        ini.AppendLine($"    {v}page = 0");
        ini.AppendLine($"    {v}key_pulse_prev = {v}key_pulse");
        ini.AppendLine($"    {v}first_frame = 0");
        ini.AppendLine("endif");
        ini.AppendLine();

        ini.AppendLine("; 呼出键脉冲：key 段用 cycle 只翻转 key_pulse，这里检测变化再翻 open。");
        ini.AppendLine("; 不能直接用 hold 的按下沿——加载/低帧率时按下与抬起可能发生在同一帧，边沿会被漏掉。");
        ini.AppendLine($"if {v}key_pulse == 1");
        ini.AppendLine($"    if {v}key_pulse_prev == 0");
        ini.AppendLine($"        if {v}open == 0");
        ini.AppendLine($"            {v}open = 1");
        ini.AppendLine("        else");
        ini.AppendLine($"            {v}open = 0");
        ini.AppendLine("        endif");
        ini.AppendLine("    endif");
        ini.AppendLine("else");
        ini.AppendLine($"    if {v}key_pulse_prev == 1");
        ini.AppendLine($"        if {v}open == 0");
        ini.AppendLine($"            {v}open = 1");
        ini.AppendLine("        else");
        ini.AppendLine($"            {v}open = 0");
        ini.AppendLine("        endif");
        ini.AppendLine("    endif");
        ini.AppendLine("endif");
        ini.AppendLine($"{v}key_pulse_prev = {v}key_pulse");
        ini.AppendLine();

        // 有 DetectionVariable 时用 Mod 自己的 object_detected（补 namespace 后已按 Mod 隔离），
        // 不再靠 hash 猜测：hash 可能命中共享资源，导致多份菜单同时亮。
        if (string.IsNullOrWhiteSpace(options.DetectionVariable)
            && !string.IsNullOrWhiteSpace(options.DetectionHash))
        {
            ini.AppendLine("; 逐角色门控：超过 0.5s 没检测到该角色模型就关闭菜单");
            ini.AppendLine($"{v}age = time - {v}last_seen");
            ini.AppendLine($"if {v}age >= 0.5");
            ini.AppendLine($"    {v}open = 0");
            ini.AppendLine("endif");
            ini.AppendLine();
        }

        // 角色不在场时自动关闭：呼出键会翻转所有菜单的 open，但只有当前角色那份能留下；
        // 其余在同帧被压回 0，避免多份菜单叠在一起，也避免切角色后突然弹出。
        if (!string.IsNullOrWhiteSpace(options.DetectionVariable))
        {
            ini.AppendLine($"if {options.DetectionVariable} == 0");
            ini.AppendLine($"    {v}open = 0");
            ini.AppendLine("endif");
            ini.AppendLine();
        }
        else if (options.GateOnObjectDetected)
        {
            ini.AppendLine("if $object_detected == 0");
            ini.AppendLine($"    {v}open = 0");
            ini.AppendLine("endif");
            ini.AppendLine();
        }

        ini.AppendLine($"if {v}open == 1");
        if (!string.IsNullOrWhiteSpace(options.DetectionVariable))
            ini.AppendLine($"if {options.DetectionVariable} == 1");
        else if (options.GateOnObjectDetected)
            ini.AppendLine("if $object_detected == 1");

        // 必须用 cursor_screen_x/y（物理屏幕像素），不能用 cursor_x/y：
        // 后者在游戏隐藏光标时**冻结**在上一次可见位置，菜单能画出来但鼠标永远落不进
        // 命中框（真机红点诊断证实）。cursor_screen_x/y 每帧从 GetCursorPos 更新。
        ini.AppendLine($"    {v}mx = cursor_screen_x");
        ini.AppendLine($"    {v}my = cursor_screen_y");
        ini.AppendLine($"    local {v}sx = res_width / 1920");
        ini.AppendLine($"    local {v}sy = res_height / 1080");
        ini.AppendLine($"    if {v}sy < {v}sx");
        ini.AppendLine($"        {v}scale = {v}sy");
        ini.AppendLine("    else");
        ini.AppendLine($"        {v}scale = {v}sx");
        ini.AppendLine("    endif");
        ini.AppendLine();
        ini.AppendLine($"    if {v}page < 0");
        ini.AppendLine($"        {v}page = 0");
        ini.AppendLine("    endif");
        ini.AppendLine($"    if {v}page > {Math.Max(0, pageCount - 1)}");
        ini.AppendLine($"        {v}page = {Math.Max(0, pageCount - 1)}");
        ini.AppendLine("    endif");
        ini.AppendLine();

        ini.AppendLine("    ; Alt+左键按下沿");
        ini.AppendLine($"    if {v}click_guard == 1");
        ini.AppendLine($"        if {v}prev_down == 0");
        ini.AppendLine($"            {v}click = 1");
        ini.AppendLine("        else");
        ini.AppendLine($"            {v}click = 0");
        ini.AppendLine("        endif");
        ini.AppendLine($"        {v}prev_down = 1");
        ini.AppendLine("    else");
        ini.AppendLine($"        {v}click = 0");
        ini.AppendLine($"        {v}prev_down = 0");
        ini.AppendLine("    endif");
        ini.AppendLine();
        ini.AppendLine("    ; Alt+右键拖动");
        ini.AppendLine($"    if {v}drag_guard == 1");
        ini.AppendLine($"        if {v}dragging == 0");
        ini.AppendLine($"            {v}dragging = 1");
        ini.AppendLine($"            {v}drag_mx = {v}mx");
        ini.AppendLine($"            {v}drag_my = {v}my");
        ini.AppendLine($"            {v}drag_x = {v}x");
        ini.AppendLine($"            {v}drag_y = {v}y");
        ini.AppendLine("        else");
        ini.AppendLine($"            {v}x = {v}drag_x + ({v}mx - {v}drag_mx) / {v}scale");
        ini.AppendLine($"            {v}y = {v}drag_y + ({v}my - {v}drag_my) / {v}scale");
        ini.AppendLine("        endif");
        ini.AppendLine("    else");
        ini.AppendLine($"        {v}dragging = 0");
        ini.AppendLine("    endif");
        ini.AppendLine();
        ini.AppendLine("    ; 位置夹取：菜单不能拖出屏幕（左侧要给竖栏留空间）");
        var menuWidth = RowWidthFor(options) + 80;
        ini.AppendLine($"    if {v}x < 80");
        ini.AppendLine($"        {v}x = 80");
        ini.AppendLine("    endif");
        ini.AppendLine($"    if {v}x > 1920 - {menuWidth}");
        ini.AppendLine($"        {v}x = 1920 - {menuWidth}");
        ini.AppendLine("    endif");
        ini.AppendLine($"    if {v}y < 0");
        ini.AppendLine($"        {v}y = 0");
        ini.AppendLine("    endif");
        ini.AppendLine($"    if {v}y > 1080 - {menuHeight}");
        ini.AppendLine($"        {v}y = 1080 - {menuHeight}");
        ini.AppendLine("    endif");
        ini.AppendLine();
        ini.AppendLine($"    run = {MenuDrawCommandList(token)}");
        // 逐个关闭门控 if：DetectionVariable 和 GateOnObjectDetected 都会额外开一层。
        // 漏掉任何一个 endif，3DMigoto 会把绘制当成无条件执行 —— 表现为「F7 改了 open 但菜单不消失」。
        if (!string.IsNullOrWhiteSpace(options.DetectionVariable) || options.GateOnObjectDetected)
            ini.AppendLine("endif");
        ini.AppendLine("endif");
        ini.AppendLine();
    }

    private static void AppendChrome(StringBuilder ini, string v, string token, int pageCount, int visibleRows,
        IReadOnlyList<ModIniToggleRow> rows, int menuHeight, InGameMenuOptions options)
    {
        ini.AppendLine($"[{MenuDrawCommandList(token)}]");

        // 整块深色背板（参考素材样式没有背板）
        if (!options.Theme.UseReferenceAssets)
        {
            AppendQuad(ini, v, token, ResourceName("Panel", token), $"{v}x + {PanelOffsetX}",
                $"{v}y + {PanelOffsetY}", PanelTextureWidth, menuHeight + PanelExtraHeight, 1);
        }

        // 标题两行 + 下划线（参考素材样式没有下划线）
        AppendQuad(ini, v, token, ResourceName("Title1", token), $"{v}x", $"{v}y", TitleWidthFor(options), TitleHeightFor(options), 1);
        AppendQuad(ini, v, token, ResourceName("Title2", token), $"{v}x", $"{v}y + {TitleHeightFor(options) + TitleLineGapFor(options)}",
            TitleWidthFor(options), TitleHeightFor(options), 1);
        if (!options.Theme.UseReferenceAssets)
        {
            AppendQuad(ini, v, token, ResourceName("Underline", token), $"{v}x", $"{v}y + {UnderlineOffsetY}",
                UnderlineWidth, UnderlineHeight, 1);
        }

        // 顶部页签（最多 6 个；更多页用左侧页码）
        var tabWidth = TabWidthFor(options);
        var tabHeight = TabHeightFor(options);
        var tabCount = Math.Min(pageCount, 6);
        for (var p = 0; p < tabCount; p++)
        {
            ini.AppendLine($"ps-t100 = {ResourceName("Tab", token)}");
            ini.AppendLine($"if {v}page == {p}");
            ini.AppendLine($"    ps-t100 = {ResourceName("TabActive", token)}");
            ini.AppendLine("endif");
            ini.AppendLine($"{v}img_w = {tabWidth}");
            ini.AppendLine($"{v}img_h = {tabHeight}");
            ini.AppendLine($"{v}img_x = {v}x + {TabsOffsetX + p * (tabWidth + TabGap)}");
            ini.AppendLine($"{v}img_y = {v}y + {TabsOffsetYFor(options)}");
            ini.AppendLine($"{v}tint_a = 1");
            ini.AppendLine($"run = {QuadCommandList(token)}");

            AppendQuad(ini, v, token, ResourceName($"TabLabel{p:00}", token),
                $"{v}x + {TabsOffsetX + p * (tabWidth + TabGap) + 5}", $"{v}y + {TabsOffsetYFor(options) + 2}",
                tabWidth - 10, tabHeight - 4, 1);

            ini.AppendLine($"{v}in_rect = 0");
            AppendHitRect(ini, v, $"{v}x + {TabsOffsetX + p * (tabWidth + TabGap)}", $"{v}y + {TabsOffsetYFor(options)}",
                tabWidth, tabHeight);
            ini.AppendLine($"if {v}in_rect == 1");
            ini.AppendLine($"    if {v}click == 1");
            ini.AppendLine($"        {v}page = {p}");
            ini.AppendLine("    endif");
            ini.AppendLine("endif");
        }

        // 关闭按钮
        AppendQuad(ini, v, token, ResourceName("Close", token), $"{v}x + {SidebarOffsetX}", $"{v}y + {CloseOffsetY}",
            CloseWidth, CloseHeight, 0.85m);
        ini.AppendLine($"{v}in_rect = 0");
        AppendHitRect(ini, v, $"{v}x + {SidebarOffsetX}", $"{v}y + {CloseOffsetY}", CloseWidth, CloseHeight);
        ini.AppendLine($"if {v}in_rect == 1");
        ini.AppendLine($"    if {v}click == 1");
        ini.AppendLine($"        {v}open = 0");
        ini.AppendLine("    endif");
        ini.AppendLine("endif");

        // 左侧竖栏 + 页码
        AppendQuad(ini, v, token, ResourceName("Sidebar", token), $"{v}x + {SidebarOffsetX}",
            $"{v}y + {SidebarOffsetY}", SidebarWidth, pageCount * SidebarNumberHeight + 12, 1);
        for (var p = 0; p < pageCount; p++)
        {
            ini.AppendLine($"{v}tint_a = 0.55");
            ini.AppendLine($"if {v}page == {p}");
            ini.AppendLine($"    {v}tint_a = 1");
            ini.AppendLine("endif");
            ini.AppendLine($"ps-t100 = {ResourceName($"Page{p:00}", token)}");
            ini.AppendLine($"{v}img_w = 32");
            ini.AppendLine($"{v}img_h = 32");
            ini.AppendLine($"{v}img_x = {v}x + {SidebarOffsetX + 6}");
            ini.AppendLine($"{v}img_y = {v}y + {SidebarOffsetY + 6 + p * SidebarNumberHeight}");
            ini.AppendLine($"run = {QuadCommandList(token)}");

            ini.AppendLine($"{v}in_rect = 0");
            AppendHitRect(ini, v, $"{v}x + {SidebarOffsetX}", $"{v}y + {SidebarOffsetY + p * SidebarNumberHeight}",
                SidebarWidth, SidebarNumberHeight);
            ini.AppendLine($"if {v}in_rect == 1");
            ini.AppendLine($"    if {v}click == 1");
            ini.AppendLine($"        {v}page = {p}");
            ini.AppendLine("    endif");
            ini.AppendLine("endif");
        }

        // 重置按钮（竖栏下方）：把所有行恢复成 Mod ini 里声明的初始值
        var resetY = SidebarOffsetY + pageCount * SidebarNumberHeight + 12 + 6;
        AppendQuad(ini, v, token, ResourceName("Reset", token), $"{v}x + {SidebarOffsetX}",
            $"{v}y + {resetY}", CloseWidth, CloseHeight, 0.85m);
        ini.AppendLine($"{v}in_rect = 0");
        AppendHitRect(ini, v, $"{v}x + {SidebarOffsetX}", $"{v}y + {resetY}", CloseWidth, CloseHeight);
        ini.AppendLine($"if {v}in_rect == 1");
        ini.AppendLine($"    if {v}click == 1");
        foreach (var row in rows)
        {
            if (row.IsClickable && !string.IsNullOrWhiteSpace(row.DefaultValue))
                ini.AppendLine($"        {row.QualifiedVariable} = {row.DefaultValue}");
        }
        ini.AppendLine("    endif");
        ini.AppendLine("endif");

        // 当前页的行
        for (var p = 0; p < pageCount; p++)
        {
            ini.AppendLine($"if {v}page == {p}");
            ini.AppendLine($"    run = {PageCommandList(token, p)}");
            ini.AppendLine("endif");
        }

        // 底部说明
        var hintY = RowOffsetYFor(options) + visibleRows * (RowHeightFor(options) + RowGap) + 8;
        AppendQuad(ini, v, token, ResourceName("HintLine1", token), $"{v}x + {BottomHintOffsetX}",
            $"{v}y + {hintY}", BottomHintWidth, BottomHintHeight, 0.8m);
        AppendQuad(ini, v, token, ResourceName("HintLine2", token), $"{v}x + {BottomHintOffsetX}",
            $"{v}y + {hintY + BottomHintLineGap}", BottomHintWidth, BottomHintHeight, 0.7m);
        AppendQuad(ini, v, token, ResourceName("HintLine3", token), $"{v}x + {BottomHintOffsetX}",
            $"{v}y + {hintY + 2 * BottomHintLineGap}", BottomHintWidth, BottomHintHeight, 0.55m);

        if (options.DebugCursor)
        {
            ini.AppendLine("; DEBUG 光标位置：红点应跟随鼠标");
            ini.AppendLine($"ps-t100 = {ResourceName("Cursor", token)}");
            ini.AppendLine($"{v}img_w = 16");
            ini.AppendLine($"{v}img_h = 16");
            ini.AppendLine($"{v}img_x = {v}mx / {v}scale - 8");
            ini.AppendLine($"{v}img_y = {v}my / {v}scale - 8");
            ini.AppendLine($"{v}tint_a = 1");
            ini.AppendLine($"run = {QuadCommandList(token)}");
        }

        ini.AppendLine();
    }

    private static void AppendPageCommandList(StringBuilder ini, string token, int page, int rowCount, int rowsPerPage)
    {
        ini.AppendLine($"[{PageCommandList(token, page)}]");
        var start = page * rowsPerPage;
        var end = Math.Min(rowCount, start + rowsPerPage);
        if (start >= end)
            ini.AppendLine("; 本页无行");
        else
            for (var i = start; i < end; i++)
                ini.AppendLine($"run = {RowCommandList(token, i)}");
        ini.AppendLine();
    }

    private static void AppendRow(StringBuilder ini, string v, string token, int index, ModIniToggleRow row,
        InGameMenuOptions options)
    {
        var rowWidth = RowWidthFor(options);
        var rowHeight = RowHeightFor(options);
        var rowsPerPage = RowsPerPage(options);
        var reference = options.Theme.UseReferenceAssets;

        var rowY = RowOffsetYFor(options) + (index % rowsPerPage) * (rowHeight + RowGap);
        var arrowOffsetY = reference ? rowHeight / 2 - ArrowHeight / 2 : ArrowOffsetY;
        var labelOffsetY = reference ? 4 : LabelOffsetY;
        var valueOffsetY = reference ? 32 : ValueOffsetY;
        var hintOffsetX = rowWidth - 150;
        var arrowRightOffsetX = rowWidth - ArrowWidth;
        var valueOffsetX = reference ? rowWidth - LabelWidthFor(options) - 32 : LabelOffsetX;

        ini.AppendLine($"; ---- 行 {index}: {SanitizeComment(row.SectionName)} → {SanitizeComment(row.Label)} ----");
        ini.AppendLine($"[{RowCommandList(token, index)}]");

        // 命中框
        ini.AppendLine($"{v}in_rect = 0");
        AppendHitRect(ini, v, $"{v}x + {RowOffsetX}", $"{v}y + {rowY}", rowWidth, rowHeight);

        // 行底条
        AppendQuad(ini, v, token, ResourceName("Row", token), $"{v}x + {RowOffsetX}", $"{v}y + {rowY}",
            rowWidth, rowHeight, 1);

        // 参考素材样式：每一行都叠米色描边（ButtonBorder），不只在悬停时
        if (reference)
        {
            AppendQuad(ini, v, token, ResourceName("RowHover", token), $"{v}x + {RowOffsetX}", $"{v}y + {rowY}",
                rowWidth, rowHeight, 1);
        }

        ini.AppendLine($"if {v}in_rect == 1");
        AppendQuad(ini, v, token, ResourceName("RowHover", token), $"{v}x + {RowOffsetX}", $"{v}y + {rowY}",
            rowWidth, rowHeight, 1);
        ini.AppendLine("endif");

        // 左箭头
        AppendQuad(ini, v, token, ResourceName("ArrowLeft", token), $"{v}x + {ArrowLeftOffsetX}",
            $"{v}y + {rowY + arrowOffsetY}", ArrowWidth, ArrowHeight, 0.9m);

        // 名称（不可点击的行调暗）
        var labelTint = row.IsClickable ? 1m : 0.45m;
        AppendQuad(ini, v, token, ResourceName($"Label{index:00}", token), $"{v}x + {LabelOffsetX}",
            $"{v}y + {rowY + labelOffsetY}", LabelWidthFor(options), LabelHeight, labelTint);

        // 当前值：按 ini 的值列表选纹理
        if (row.IsClickable)
        {
            ini.AppendLine($"ps-t100 = {ResourceName($"Value{index:00}_00", token)}");
            for (var valueIndex = 1; valueIndex < row.Values.Count; valueIndex++)
            {
                ini.AppendLine($"if {row.QualifiedVariable} == {row.Values[valueIndex]}");
                ini.AppendLine($"    ps-t100 = {ResourceName($"Value{index:00}_{valueIndex:00}", token)}");
                ini.AppendLine("endif");
            }

            ini.AppendLine($"{v}img_w = {LabelWidthFor(options)}");
            ini.AppendLine($"{v}img_h = {ValueHeight}");
            ini.AppendLine($"{v}img_x = {v}x + {valueOffsetX}");
            ini.AppendLine($"{v}img_y = {v}y + {rowY + valueOffsetY}");
            ini.AppendLine($"{v}tint_a = 0.85");
            ini.AppendLine($"run = {QuadCommandList(token)}");
        }

        // 原按键提示
        if (!string.IsNullOrWhiteSpace(row.KeyDisplay))
        {
            AppendQuad(ini, v, token, ResourceName($"Hint{index:00}", token), $"{v}x + {hintOffsetX}",
                $"{v}y + {rowY + HintOffsetYFor(options)}", HintWidth, HintHeight, 0.7m);
        }

        // 右箭头
        AppendQuad(ini, v, token, ResourceName("ArrowRight", token), $"{v}x + {arrowRightOffsetX}",
            $"{v}y + {rowY + arrowOffsetY}", ArrowWidth, ArrowHeight, 0.9m);

        // Alt+左键点击切换
        if (row.IsClickable)
        {
            ini.AppendLine($"if {v}in_rect == 1");
            ini.AppendLine($"    if {v}click == 1");
            AppendCycle(ini, row.QualifiedVariable, row.Values);
            ini.AppendLine("    endif");
            ini.AppendLine("endif");
        }

        ini.AppendLine();
    }

    private static void AppendHitRect(StringBuilder ini, string v, string xExpression, string yExpression, int width,
        int height)
    {
        ini.AppendLine($"if {v}mx >= ({xExpression}) * {v}scale");
        ini.AppendLine($"    if {v}mx <= ({xExpression} + {width}) * {v}scale");
        ini.AppendLine($"        if {v}my >= ({yExpression}) * {v}scale");
        ini.AppendLine($"            if {v}my <= ({yExpression} + {height}) * {v}scale");
        ini.AppendLine($"                {v}in_rect = 1");
        ini.AppendLine("            endif");
        ini.AppendLine("        endif");
        ini.AppendLine("    endif");
        ini.AppendLine("endif");
    }

    private static void AppendQuad(StringBuilder ini, string v, string token, string resource, string xExpression,
        string yExpression, int width, int height, decimal alpha)
    {
        ini.AppendLine($"ps-t100 = {resource}");
        ini.AppendLine($"{v}img_w = {width}");
        ini.AppendLine($"{v}img_h = {height}");
        ini.AppendLine($"{v}img_x = {xExpression}");
        ini.AppendLine($"{v}img_y = {yExpression}");
        ini.AppendLine($"{v}tint_a = {alpha.ToString(CultureInfo.InvariantCulture)}");
        ini.AppendLine($"run = {QuadCommandList(token)}");
    }

    /// <summary>生成「按一次键」的等价 if/else 链，支持任意顺序与非连续值（4,8,9）。</summary>
    private static void AppendCycle(StringBuilder ini, string qualifiedVariable, IReadOnlyList<string> values)
    {
        var indent = "        ";

        for (var i = 0; i < values.Count; i++)
        {
            var next = values[(i + 1) % values.Count];
            ini.AppendLine($"{indent}if {qualifiedVariable} == {values[i]}");
            ini.AppendLine($"{indent}    {qualifiedVariable} = {next}");
            ini.AppendLine($"{indent}else");

            if (i == values.Count - 1)
                ini.AppendLine($"{indent}    {qualifiedVariable} = {values[0]}");

            indent += "    ";
        }

        for (var i = values.Count - 1; i >= 0; i--)
        {
            indent = indent[..^4];
            ini.AppendLine($"{indent}endif");
        }
    }

    private static void AppendResources(StringBuilder ini, string token, IReadOnlyList<ModIniToggleRow> rows,
        int pageCount, InGameMenuOptions options)
    {
        ini.AppendLine();
        ini.AppendLine("; ============================================================");
        ini.AppendLine("; 资源");
        ini.AppendLine("; ============================================================");

        // 参考素材模式下，行/页签直接引用拷贝来的 .png；其余仍用生成的 .dds
        void Res(string name, string ddsPath)
            => AppendResource(ini, token, name,
                options.PngResources.Contains(name, StringComparer.OrdinalIgnoreCase)
                    ? Path.ChangeExtension(ddsPath, ".png")
                    : ddsPath);

        Res("Title1", "JASM_Menu/title_1.dds");
        Res("Title2", "JASM_Menu/title_2.dds");
        Res("Underline", "JASM_Menu/underline.dds");
        Res("Tab", "JASM_Menu/tab.dds");
        Res("TabActive", "JASM_Menu/tab_active.dds");
        Res("Sidebar", "JASM_Menu/sidebar.dds");
        Res("Close", "JASM_Menu/close.dds");
        Res("Reset", "JASM_Menu/reset.dds");
        Res("Panel", "JASM_Menu/panel.dds");
        Res("Row", "JASM_Menu/row.dds");
        Res("RowHover", "JASM_Menu/row_hover.dds");
        Res("ArrowLeft", "JASM_Menu/arrow_left.dds");
        Res("ArrowRight", "JASM_Menu/arrow_right.dds");
        Res("HintLine1", "JASM_Menu/hint_1.dds");
        Res("HintLine2", "JASM_Menu/hint_2.dds");
        Res("HintLine3", "JASM_Menu/hint_3.dds");

        for (var p = 0; p < pageCount; p++)
        {
            Res($"TabLabel{p:00}", $"JASM_Menu/tab_label_{p:00}.dds");
            Res($"Page{p:00}", $"JASM_Menu/page_{p:00}.dds");
        }

        for (var i = 0; i < rows.Count; i++)
        {
            Res($"Label{i:00}", $"JASM_Menu/label_{i:00}.dds");

            if (!string.IsNullOrWhiteSpace(rows[i].KeyDisplay))
                Res($"Hint{i:00}", $"JASM_Menu/hint_{i:00}.dds");

            for (var valueIndex = 0; valueIndex < rows[i].Values.Count; valueIndex++)
            {
                Res($"Value{i:00}_{valueIndex:00}",
                    $"JASM_Menu/value_{i:00}_{valueIndex:00}.dds");
            }
        }

        if (options.DebugCursor)
            Res("Cursor", "JASM_Menu/cursor.dds");
    }

    private static void AppendResource(StringBuilder ini, string token, string name, string fileName)
    {
        ini.AppendLine($"[{ResourceName(name, token)}]");
        ini.AppendLine($"filename = {fileName}");
    }

    private static void AppendShader(StringBuilder ini, string v, string token)
    {
        ini.AppendLine();
        ini.AppendLine("; ============================================================");
        ini.AppendLine("; 2D 贴图绘制（自研，随菜单一起生成）");
        ini.AppendLine("; IniParams[87] = 尺寸/偏移（归一化），IniParams[88] = 着色");
        ini.AppendLine("; ============================================================");
        ini.AppendLine($"[{QuadCommandList(token)}]");
        ini.AppendLine("vs = JASM_Menu/draw_2d.hlsl");
        ini.AppendLine("ps = JASM_Menu/draw_2d.hlsl");
        ini.AppendLine($"local {v}quad_w = {v}img_w * {v}scale / res_width");
        ini.AppendLine($"local {v}quad_h = {v}img_h * {v}scale / res_height");
        ini.AppendLine($"local {v}quad_x = {v}img_x * {v}scale / res_width");
        ini.AppendLine($"local {v}quad_y = {v}img_y * {v}scale / res_height");
        ini.AppendLine($"x87 = {v}quad_w");
        ini.AppendLine($"y87 = {v}quad_h");
        ini.AppendLine($"z87 = {v}quad_x");
        ini.AppendLine($"w87 = {v}quad_y");
        ini.AppendLine("x88 = 1");
        ini.AppendLine("y88 = 1");
        ini.AppendLine("z88 = 1");
        ini.AppendLine($"w88 = {v}tint_a");
        ini.AppendLine("blend    = ADD SRC_ALPHA INV_SRC_ALPHA");
        ini.AppendLine("cull     = none");
        ini.AppendLine("topology = triangle_strip");
        ini.AppendLine("o0       = set_viewport bb");
        ini.AppendLine("Draw     = 4,0");
        ini.AppendLine("clear    = ps-t100");
    }

    private static (string Line1, string Line2) SplitTitle(string title)
    {
        var trimmed = (title ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return ("JASM", string.Empty);

        // 「角色-皮肤」形式拆成两行（截图里的 清宵 / 绯花令）
        var separator = trimmed.IndexOfAny(['-', '—', '|', '｜', '·']);
        if (separator > 0 && separator < trimmed.Length - 1)
        {
            var left = trimmed[..separator].Trim();
            var right = trimmed[(separator + 1)..].Trim();
            if (left.Length > 0 && right.Length > 0)
                return (left, right);
        }

        if (trimmed.Length <= 10)
            return (trimmed, string.Empty);

        return (trimmed[..10], trimmed[10..]);
    }

    private static string ResourceName(string name, string token) => $"ResourceJasm{name}_{token}";

    private static string MenuNamespace(string token) => $"JasmMenu{token}";

    private static string KeyToggleSection(string token) => $"KeyJasmMenuToggle_{token}";

    private static string KeyMouseClickSection(string token) => $"KeyJasmMenuMouseClick_{token}";

    private static string KeyMouseDragSection(string token) => $"KeyJasmMenuMouseDrag_{token}";

    private static string MenuDrawCommandList(string token) => $"CommandListJasmMenuDraw_{token}";

    private static string DetectorSection(string token) => $"TextureOverrideJasmDetect_{token}";

    private static string EntryCommandList(string token) => $"CommandListJasmEntry_{token}";

    private static string QuadCommandList(string token) => $"CustomShaderJasmQuad_{token}";

    private static string PageCommandList(string token, int page) => $"CommandListJasmPage{page}_{token}";

    private static string RowCommandList(string token, int index) => $"CommandListJasmRow{index}_{token}";

    private static string NormalizeToken(string token)
    {
        var cleaned = new string(token.Where(char.IsLetterOrDigit).ToArray());
        return cleaned.Length == 0 ? "menu" : cleaned.ToLowerInvariant();
    }

    private static string SanitizeComment(string text)
        => text.Replace('\r', ' ').Replace('\n', ' ').Replace(';', ':');

    private static string Unquote(string value)
        => value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;
}
