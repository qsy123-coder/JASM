using System.Globalization;
using System.Text;

namespace GIMI_ModManager.Core.Entities.Mods.InGameMenu;

/// <summary>生成菜单时的可配置项。</summary>
public sealed record InGameMenuOptions
{
    /// <summary>3DMigoto <c>[Key*]</c> 里写的组合键，如 <c>CTRL SHIFT VK_M</c>。</summary>
    public string OpenKeyTokens { get; init; } = "CTRL SHIFT VK_M";

    /// <summary>UI 上显示的同一个键，如 <c>Ctrl+Shift+M</c>（用于菜单右上角提示）。</summary>
    public string OpenKeyDisplay { get; init; } = "Ctrl+Shift+M";

    /// <summary>菜单标题（通常是 Mod 显示名）。</summary>
    public string Title { get; init; } = "JASM";

    /// <summary>一页最多几行。</summary>
    public int MaxRowsPerPage { get; init; } = 10;

    /// <summary>Mod 是否提供 <c>$object_detected</c>；没有就不加门控。</summary>
    public bool GateOnObjectDetected { get; init; } = true;

    /// <summary>生成器版本，写进 ini 头与 manifest，便于日后失效重生成。</summary>
    public int GeneratorVersion { get; init; } = 1;

    /// <summary>
    /// 诊断用：在光标计算位置画一个红色小方块。用来区分「坐标算错」与「游戏锁住光标」。
    /// </summary>
    public bool DebugCursor { get; init; }
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
    Panel,
    Hover,
    Text,
    Marker,
}

/// <summary><see cref="InGameMenuIniBuilder.Build"/> 的产物：ini 文本 + 需要落盘的纹理清单。</summary>
public sealed record InGameMenuBuildResult(
    string IniText,
    IReadOnlyList<MenuTextureSpec> Textures,
    int PanelWidth,
    int PanelHeight,
    int PageCount);

/// <summary>
/// 把解析出来的行拼成 3DMigoto 能加载的 <c>JASM_Menu.ini</c>。
///
/// 关键设计（都来自本机可点击菜单与跨命名空间变量的实测写法）：
/// • 菜单文件**不声明 namespace**：这样没有命名空间的 Mod（全局变量）可以直接用 <c>$var</c> 读写；
/// • 有命名空间的 Mod 用全限定名 <c>$\Namespace\var</c>（Mod ini 里跨文件赋值就是这么写的）；
/// • 因为不声明 namespace，菜单自己的变量与段落名都带唯一 token 后缀，避免多个菜单互相覆盖；
/// • 点击等价于「作者按了一次键」：按 ini 的值列表做 if/else 循环，不猜语义；
/// • 所有坐标都写在 1920×1080 的虚拟空间里，运行时按 <c>min(res_w/1920, res_h/1080)</c> 缩放。
/// </summary>
public static class InGameMenuIniBuilder
{
    public const int MenuWidth = 560;
    public const int RowHeight = 52;
    public const int RowsStartY = 64;
    public const int HeaderHeight = 64;
    public const int FooterHeight = 56;
    public const int DefaultRowsPerPage = 10;

    private const int LabelX = 24;
    private const int LabelWidth = 320;
    private const int ValueX = 348;
    private const int ValueWidth = 110;
    private const int KeyX = 462;
    private const int KeyWidth = 82;

    /// <summary>
    /// <paramref name="menuToken"/> 是每个 Mod 唯一的短后缀（如 <c>4aaf3646</c>），
    /// 会拼进所有自有变量与段落名，保证多个菜单在根命名空间里不冲突。
    /// </summary>
    public static InGameMenuBuildResult Build(
        IReadOnlyList<ModIniToggleRow> rows,
        string menuToken,
        InGameMenuOptions options)
    {
        var token = NormalizeToken(menuToken);
        var v = "$jasm_" + token + "_"; // 自有变量前缀

        var rowsPerPage = Math.Clamp(options.MaxRowsPerPage, 1, 20);
        options = options with { MaxRowsPerPage = rowsPerPage };

        var rowCount = rows.Count;
        var pageCount = Math.Max(1, (rowCount + rowsPerPage - 1) / rowsPerPage);
        var visibleRows = Math.Min(rowsPerPage, Math.Max(1, rowCount));
        var panelHeight = HeaderHeight + visibleRows * RowHeight + FooterHeight;

        var textures = new List<MenuTextureSpec>
        {
            new("JASM_Menu/panel.dds", MenuTextureKind.Panel, string.Empty, MenuWidth, panelHeight, 0, false,
                GdiTextRasterizer.TextAlign.Left),
            new("JASM_Menu/hover.dds", MenuTextureKind.Hover, string.Empty, MenuWidth - 16, RowHeight - 4, 0,
                false, GdiTextRasterizer.TextAlign.Left),
            new("JASM_Menu/title.dds", MenuTextureKind.Text, options.Title, 380, 36, 26, true,
                GdiTextRasterizer.TextAlign.Left),
            new("JASM_Menu/hint.dds", MenuTextureKind.Text, options.OpenKeyDisplay, 140, 28, 18, false,
                GdiTextRasterizer.TextAlign.Right),
        };

        if (options.DebugCursor)
        {
            textures.Add(new MenuTextureSpec("JASM_Menu/cursor.dds", MenuTextureKind.Marker, string.Empty, 16, 16, 0,
                false, GdiTextRasterizer.TextAlign.Left));
        }

        var ini = new StringBuilder(64 * 1024);

        ini.AppendLine("; ============================================================");
        ini.AppendLine("; JASM 游戏内切换菜单（自动生成，请勿手改）");
        ini.AppendLine("; 删除本文件与 JASM_Menu 目录即可完全移除；作者原有 ini 未被修改");
        ini.AppendLine($"; generator = {options.GeneratorVersion}   rows = {rowCount}   pages = {pageCount}");
        ini.AppendLine("; ============================================================");
        ini.AppendLine();

        // 必须给 ASCII namespace：没有 namespace 时 3DMigoto 用 ini 路径当变量命名空间，
        // 而 Mod 路径常含中文，变量名会被判非法（日志：Unknown global variable），
        // 连带 if 门控解析失败、菜单无条件绘制。
        ini.AppendLine($"namespace = {MenuNamespace(token)}");
        ini.AppendLine();

        // 顺序很重要：3DMigoto 会先处理 [Key*] 里的循环声明（$var = 0,1），
        // 再由后面的 [Constants] 覆盖初始值。参考实现就是「按键段在前、Constants 在后」；
        // 反过来的话菜单会在启动时直接处于打开状态。
        ini.AppendLine($"[{KeyToggleSection(token)}]");
        ini.AppendLine($"key = {options.OpenKeyTokens}");
        ini.AppendLine("type = cycle");
        ini.AppendLine($"{v}open = 0,1");
        ini.AppendLine();

        // 键盘导航：游戏内光标被锁死时鼠标不可用，这是唯一可靠的路径
        ini.AppendLine($"[{KeyUpSection(token)}]");
        ini.AppendLine($"condition = {v}open == 1");
        ini.AppendLine("key = UP");
        ini.AppendLine($"{v}sel = {v}sel - 1");
        ini.AppendLine();
        ini.AppendLine($"[{KeyDownSection(token)}]");
        ini.AppendLine($"condition = {v}open == 1");
        ini.AppendLine("key = DOWN");
        ini.AppendLine($"{v}sel = {v}sel + 1");
        ini.AppendLine();
        ini.AppendLine($"[{KeyActivateSection(token)}]");
        ini.AppendLine($"condition = {v}open == 1");
        ini.AppendLine("key = F9");
        ini.AppendLine($"run = {ActivateCommandList(token)}");
        ini.AppendLine();

        AppendConstants(ini, v, pageCount);

        AppendPresent(ini, v, token, options, pageCount, rowCount, rowsPerPage);
        AppendDrawRoot(ini, v, token, pageCount, panelHeight, options.DebugCursor);

        for (var page = 0; page < pageCount; page++)
            AppendPageCommandList(ini, token, page, rowCount, rowsPerPage);

        for (var i = 0; i < rowCount; i++)
        {
            AppendRow(ini, v, token, i, rows[i], options);

            textures.Add(new MenuTextureSpec($"JASM_Menu/label_{i:00}.dds", MenuTextureKind.Text,
                rows[i].Label, LabelWidth, 36, 22, false, GdiTextRasterizer.TextAlign.Left));

            if (!string.IsNullOrWhiteSpace(rows[i].KeyDisplay))
            {
                textures.Add(new MenuTextureSpec($"JASM_Menu/key_{i:00}.dds", MenuTextureKind.Text,
                    rows[i].KeyDisplay, KeyWidth, 36, 17, false, GdiTextRasterizer.TextAlign.Right));
            }

            for (var valueIndex = 0; valueIndex < rows[i].Values.Count; valueIndex++)
            {
                textures.Add(new MenuTextureSpec($"JASM_Menu/value_{i:00}_{valueIndex:00}.dds", MenuTextureKind.Text,
                    Unquote(rows[i].Values[valueIndex]), ValueWidth, 36, 20, false,
                    GdiTextRasterizer.TextAlign.Right));
            }
        }

        AppendFooter(ini, v, token, pageCount, panelHeight);

        AppendActivateCommandList(ini, rows, token, v);

        for (var p = 0; p < pageCount; p++)
        {
            textures.Add(new MenuTextureSpec($"JASM_Menu/page_{p:00}.dds", MenuTextureKind.Text,
                $"{p + 1}/{pageCount}", 100, 28, 18, false, GdiTextRasterizer.TextAlign.Center));
        }

        if (pageCount > 1)
        {
            textures.Add(new MenuTextureSpec("JASM_Menu/arrow_prev.dds", MenuTextureKind.Text, "‹", 32, 32, 28,
                false, GdiTextRasterizer.TextAlign.Center));
            textures.Add(new MenuTextureSpec("JASM_Menu/arrow_next.dds", MenuTextureKind.Text, "›", 32, 32, 28,
                false, GdiTextRasterizer.TextAlign.Center));
        }

        AppendResources(ini, token, rows, pageCount, options.DebugCursor);
        AppendShader(ini, v, token);

        return new InGameMenuBuildResult(ini.ToString(), textures, MenuWidth, panelHeight, pageCount);
    }

    private static void AppendConstants(StringBuilder ini, string v, int pageCount)
    {
        ini.AppendLine("[Constants]");
        // open/page 不做 persist：否则上次退出时菜单开着，下次一进游戏就直接盖在屏幕上，
        // 用户按一次键反而变成「关闭」，看起来像热键失效。位置 x/y 才需要记住。
        ini.AppendLine($"global {v}open = 0");
        ini.AppendLine($"global persist {v}x = 420");
        ini.AppendLine($"global persist {v}y = 120");
        ini.AppendLine($"global {v}page = 0");
        ini.AppendLine($"global {v}prev_down = 0");
        ini.AppendLine($"global {v}click = 0");
        ini.AppendLine($"global {v}mx = 0");
        ini.AppendLine($"global {v}my = 0");
        ini.AppendLine($"global {v}scale = 1");
        ini.AppendLine($"global {v}in_rect = 0");
        ini.AppendLine($"global {v}sel = 0");
        ini.AppendLine($"global {v}dragging = 0");
        ini.AppendLine($"global {v}drag_mx = 0");
        ini.AppendLine($"global {v}drag_my = 0");
        ini.AppendLine($"global {v}drag_x = 0");
        ini.AppendLine($"global {v}drag_y = 0");
        ini.AppendLine($"global {v}img_w = 0");
        ini.AppendLine($"global {v}img_h = 0");
        ini.AppendLine($"global {v}img_x = 0");
        ini.AppendLine($"global {v}img_y = 0");
        ini.AppendLine($"global {v}tint_a = 1");
        ini.AppendLine($"global {v}page_count = {pageCount}");
        // 首帧强制归零：不依赖 [Key*] 循环声明的初始值，避免「一进游戏菜单自己弹出来」
        ini.AppendLine($"global {v}first_frame = 1");
        ini.AppendLine();
    }

    private static void AppendPresent(StringBuilder ini, string v, string token, InGameMenuOptions options,
        int pageCount, int rowCount, int rowsPerPage)
    {
        // [Present] 只做无条件转发：把 if 门控放进 CommandList 里。
        // 实机验证发现：非命名空间的 mod ini 里，直接写在 [Present] 中的 if 块不会生效
        // （菜单会无条件绘制），而 CommandList 内部的 if 是正常的。
        ini.AppendLine("[Present]");
        ini.AppendLine($"run = {EntryCommandList(token)}");
        ini.AppendLine();
        ini.AppendLine($"[{EntryCommandList(token)}]");
        ini.AppendLine("; 首帧把菜单状态压回关闭（F10 重载后也成立）");
        ini.AppendLine($"if {v}first_frame == 1");
        ini.AppendLine($"    {v}open = 0");
        ini.AppendLine($"    {v}page = 0");
        ini.AppendLine($"    {v}first_frame = 0");
        ini.AppendLine("endif");
        ini.AppendLine();
        ini.AppendLine("; 键盘选中行夹取（↑/↓ 只改 sel，这里保证不越界）");
        ini.AppendLine($"if {v}sel < 0");
        ini.AppendLine($"    {v}sel = 0");
        ini.AppendLine("endif");
        ini.AppendLine($"if {v}sel > {Math.Max(0, rowCount - 1)}");
        ini.AppendLine($"    {v}sel = {Math.Max(0, rowCount - 1)}");
        ini.AppendLine("endif");
        ini.AppendLine();
        ini.AppendLine("; 键盘选中行自动切页");
        for (var p = 1; p < pageCount; p++)
        {
            ini.AppendLine($"if {v}sel >= {p * rowsPerPage}");
            ini.AppendLine($"    {v}page = {p}");
            ini.AppendLine("endif");
        }
        ini.AppendLine();
        ini.AppendLine("; 鼠标点击已停用（游戏内光标被锁定）；交互走 ↑/↓ + F9");
        ini.AppendLine($"if {v}open == 1");
        if (options.GateOnObjectDetected)
            ini.AppendLine("if $object_detected == 1");

        // cursor_x/y 是 GIMI 分支的归一化光标变量（参考菜单在用，已确认本机这个构建能解析）。
        // 先保留它 + 红点诊断：如果红点不跟手，就是游戏锁光标，而不是坐标换算问题。
        ini.AppendLine($"    {v}mx = cursor_x * res_width");
        ini.AppendLine($"    {v}my = cursor_y * res_height");
        ini.AppendLine($"    local {v}sx = res_width / 1920");
        ini.AppendLine($"    local {v}sy = res_height / 1080");
        ini.AppendLine($"    if {v}sy < {v}sx");
        ini.AppendLine($"        {v}scale = {v}sy");
        ini.AppendLine("    else");
        ini.AppendLine($"        {v}scale = {v}sx");
        ini.AppendLine("    endif");
        ini.AppendLine();
        ini.AppendLine("    ; 翻页兜底（分辨率/配置切换后 page 可能越界）");
        ini.AppendLine($"    if {v}page < 0");
        ini.AppendLine($"        {v}page = 0");
        ini.AppendLine("    endif");
        ini.AppendLine($"    if {v}page > {Math.Max(0, pageCount - 1)}");
        ini.AppendLine($"        {v}page = {Math.Max(0, pageCount - 1)}");
        ini.AppendLine("    endif");
        ini.AppendLine();
        // 拖动/鼠标点击已全部移除：cursor_x/y 在鸣潮里不可用，
        // 且引用未声明的变量会让 3DMigoto 直接丢掉后面的 if/endif 结构（门控失效）。
        ini.AppendLine($"    run = {MenuDrawCommandList(token)}");
        if (options.GateOnObjectDetected)
            ini.AppendLine("endif");
        ini.AppendLine("endif");
        ini.AppendLine();
    }

    private static void AppendDrawRoot(StringBuilder ini, string v, string token, int pageCount, int panelHeight,
        bool debugCursor)
    {
        ini.AppendLine($"[{MenuDrawCommandList(token)}]");
        AppendQuad(ini, v, token, ResourceName("Panel", token), $"{v}x", $"{v}y", MenuWidth, panelHeight, 1);
        AppendQuad(ini, v, token, ResourceName("Title", token), $"{v}x + 20", $"{v}y + 14", 380, 36, 1);
        AppendQuad(ini, v, token, ResourceName("Hint", token), $"{v}x + 404", $"{v}y + 18", 140, 28, 0.55m);
        ini.AppendLine();

        // 平铺 if：同一帧只有一个页面条件成立，比嵌套 else 更好生成/更好读
        for (var page = 0; page < pageCount; page++)
        {
            ini.AppendLine($"if {v}page == {page}");
            ini.AppendLine($"    run = {PageCommandList(token, page)}");
            ini.AppendLine("endif");
        }

        if (pageCount > 1)
            ini.AppendLine($"run = {FooterCommandList(token)}");

        if (debugCursor)
        {
            ini.AppendLine("; DEBUG 光标位置：红点应跟随鼠标；若它不动，说明游戏锁住了系统光标");
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

        // 少于一行也要有内容，否则空段落在某些 3DMigoto 版本上会被警告
        if (start >= end)
            ini.AppendLine("; 本页无行");
        else
            for (var rowIndex = start; rowIndex < end; rowIndex++)
                ini.AppendLine($"run = {RowCommandList(token, rowIndex)}");

        ini.AppendLine();
    }

    private static void AppendRow(StringBuilder ini, string v, string token, int index, ModIniToggleRow row,
        InGameMenuOptions options)
    {
        var rowOffset = RowsStartY + (index % options.MaxRowsPerPage) * RowHeight;
        var labelY = rowOffset + 8;

        ini.AppendLine($"; ---- 行 {index}: {SanitizeComment(row.SectionName)} → {SanitizeComment(row.Label)} ----");
        ini.AppendLine($"[{RowCommandList(token, index)}]");

        // 命中框
        ini.AppendLine($"{v}in_rect = 0");
        ini.AppendLine($"if {v}mx >= ({v}x + 8) * {v}scale");
        ini.AppendLine($"    if {v}mx <= ({v}x + {MenuWidth - 8}) * {v}scale");
        ini.AppendLine($"        if {v}my >= ({v}y + {rowOffset + 2}) * {v}scale");
        ini.AppendLine($"            if {v}my <= ({v}y + {rowOffset + RowHeight - 2}) * {v}scale");
        ini.AppendLine($"                {v}in_rect = 1");
        ini.AppendLine("            endif");
        ini.AppendLine("        endif");
        ini.AppendLine("    endif");
        ini.AppendLine("endif");

        // 悬停高亮
        ini.AppendLine($"if {v}in_rect == 1");
        AppendQuad(ini, v, token, ResourceName("Hover", token), $"{v}x + 8", $"{v}y + {rowOffset + 2}",
            MenuWidth - 16, RowHeight - 4, 1);
        ini.AppendLine("endif");

        // 键盘选中高亮（与悬停叠加，选中更亮）
        ini.AppendLine($"if {v}sel == {index}");
        AppendQuad(ini, v, token, ResourceName("Hover", token), $"{v}x + 8", $"{v}y + {rowOffset + 2}",
            MenuWidth - 16, RowHeight - 4, 1);
        ini.AppendLine("endif");

        // 标签（不可点击的行调暗）
        var labelTint = row.IsClickable ? 1m : 0.42m;
        AppendQuad(ini, v, token, ResourceName($"Label{index:00}", token), $"{v}x + {LabelX}",
            $"{v}y + {labelY}", LabelWidth, 36, labelTint);

        // 当前值
        if (row.IsClickable)
        {
            ini.AppendLine("; 当前值：按 ini 的候选值顺序选纹理");
            ini.AppendLine($"ps-t100 = {ResourceName($"Value{index:00}_00", token)}");

            for (var valueIndex = 1; valueIndex < row.Values.Count; valueIndex++)
            {
                ini.AppendLine($"if {row.QualifiedVariable} == {row.Values[valueIndex]}");
                ini.AppendLine($"    ps-t100 = {ResourceName($"Value{index:00}_{valueIndex:00}", token)}");
                ini.AppendLine("endif");
            }

            ini.AppendLine($"{v}img_w = {ValueWidth}");
            ini.AppendLine($"{v}img_h = 36");
            ini.AppendLine($"{v}img_x = {v}x + {ValueX}");
            ini.AppendLine($"{v}img_y = {v}y + {labelY}");
            ini.AppendLine($"{v}tint_a = 0.9");
            ini.AppendLine($"run = {QuadCommandList(token)}");
        }

        // 原按键提示
        if (!string.IsNullOrWhiteSpace(row.KeyDisplay))
        {
            AppendQuad(ini, v, token, ResourceName($"Key{index:00}", token), $"{v}x + {KeyX}",
                $"{v}y + {labelY}", KeyWidth, 36, 0.45m);
        }

        // 点击切换已在键盘导航版本中移除（游戏内光标不可用）。
        // 变量循环统一由 CommandListJasmActivate 处理。

        ini.AppendLine();
    }

    /// <summary>
    /// 键盘确认键（F9）的执行体：按当前选中行分发到对应的变量循环。
    /// 用平铺 if 而不是查表：3DMigoto 没有数组，而选中行数很小。
    /// </summary>
    private static void AppendActivateCommandList(StringBuilder ini, IReadOnlyList<ModIniToggleRow> rows,
        string token, string v)
    {
        ini.AppendLine($"[{ActivateCommandList(token)}]");

        var any = false;
        for (var i = 0; i < rows.Count; i++)
        {
            if (!rows[i].IsClickable)
                continue;

            any = true;
            ini.AppendLine($"if {v}sel == {i}");
            AppendCycle(ini, rows[i].QualifiedVariable, rows[i].Values);
            ini.AppendLine("endif");
        }

        if (!any)
            ini.AppendLine("; 无可点击行");

        ini.AppendLine();
    }

    /// <summary>生成「按一次键」的等价 if/else 链，支持任意顺序与非连续值（4,8,9）。</summary>
    private static void AppendCycle(StringBuilder ini, string qualifiedVariable, IReadOnlyList<string> values)
    {
        var indent = "    ";

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

        // 收尾：逐个 endif（最后一层已经在上面写了 else 分支的值）
        for (var i = values.Count - 1; i >= 0; i--)
        {
            indent = indent[..^4];
            ini.AppendLine($"{indent}endif");
        }
    }

    private static void AppendFooter(StringBuilder ini, string v, string token, int pageCount, int panelHeight)
    {
        if (pageCount <= 1)
            return;

        // Footer 是一个 CommandList：它会在菜单绘制命令里被显式调用。
        // 直接写在段落下方的裸语句 3DMigoto 不会执行。
        ini.AppendLine($"[{FooterCommandList(token)}]");
        var footerY = panelHeight - FooterHeight + 8;
        var centerX = MenuWidth / 2;

        ini.AppendLine("; ---- 翻页（键盘选中行会自动切页；箭头仅显示） ----");
        AppendQuad(ini, v, token, ResourceName("Prev", token), $"{v}x + {centerX - 118}", $"{v}y + {footerY}",
            32, 32, 0.75m);
        AppendQuad(ini, v, token, ResourceName("Next", token), $"{v}x + {centerX + 86}", $"{v}y + {footerY}",
            32, 32, 0.75m);

        // 页码
        ini.AppendLine($"ps-t100 = {ResourceName("Page00", token)}");
        for (var p = 1; p < pageCount; p++)
        {
            ini.AppendLine($"if {v}page == {p}");
            ini.AppendLine($"    ps-t100 = {ResourceName($"Page{p:00}", token)}");
            ini.AppendLine("endif");
        }

        ini.AppendLine($"{v}img_w = 100");
        ini.AppendLine($"{v}img_h = 28");
        ini.AppendLine($"{v}img_x = {v}x + {centerX - 50}");
        ini.AppendLine($"{v}img_y = {v}y + {footerY + 2}");
        ini.AppendLine($"{v}tint_a = 0.65");
        ini.AppendLine($"run = {QuadCommandList(token)}");
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

    private static void AppendResources(StringBuilder ini, string token, IReadOnlyList<ModIniToggleRow> rows,
        int pageCount, bool debugCursor)
    {
        ini.AppendLine();
        ini.AppendLine("; ============================================================");
        ini.AppendLine("; 资源");
        ini.AppendLine("; ============================================================");
        AppendResource(ini, token, "Panel", "JASM_Menu/panel.dds");
        AppendResource(ini, token, "Hover", "JASM_Menu/hover.dds");
        AppendResource(ini, token, "Title", "JASM_Menu/title.dds");
        AppendResource(ini, token, "Hint", "JASM_Menu/hint.dds");

        for (var i = 0; i < rows.Count; i++)
        {
            AppendResource(ini, token, $"Label{i:00}", $"JASM_Menu/label_{i:00}.dds");

            if (!string.IsNullOrWhiteSpace(rows[i].KeyDisplay))
                AppendResource(ini, token, $"Key{i:00}", $"JASM_Menu/key_{i:00}.dds");

            for (var valueIndex = 0; valueIndex < rows[i].Values.Count; valueIndex++)
                AppendResource(ini, token, $"Value{i:00}_{valueIndex:00}",
                    $"JASM_Menu/value_{i:00}_{valueIndex:00}.dds");
        }

        for (var p = 0; p < pageCount; p++)
            AppendResource(ini, token, $"Page{p:00}", $"JASM_Menu/page_{p:00}.dds");

        if (pageCount > 1)
        {
            AppendResource(ini, token, "Prev", "JASM_Menu/arrow_prev.dds");
            AppendResource(ini, token, "Next", "JASM_Menu/arrow_next.dds");
        }

        if (debugCursor)
            AppendResource(ini, token, "Cursor", "JASM_Menu/cursor.dds");
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

    private static string ResourceName(string name, string token) => $"ResourceJasm{name}_{token}";

    /// <summary>ASCII 命名空间：避免路径里的中文进入变量名。</summary>
    private static string MenuNamespace(string token) => $"JasmMenu{token}";

    private static string KeyToggleSection(string token) => $"KeyJasmMenuToggle_{token}";

    private static string KeyClickSection(string token) => $"KeyJasmMenuClick_{token}";

    private static string KeyUpSection(string token) => $"KeyJasmMenuUp_{token}";

    private static string KeyDownSection(string token) => $"KeyJasmMenuDown_{token}";

    private static string KeyActivateSection(string token) => $"KeyJasmMenuActivate_{token}";

    /// <summary>键盘确认键的执行体：按当前选中行分发到对应的变量循环。</summary>
    private static string ActivateCommandList(string token) => $"CommandListJasmActivate_{token}";

    private static string MenuDrawCommandList(string token) => $"CommandListJasmMenuDraw_{token}";

    /// <summary>[Present] 无条件调用的入口，所有 if 门控都在这个 CommandList 内部。</summary>
    private static string EntryCommandList(string token) => $"CommandListJasmEntry_{token}";

    /// <summary>
    /// vs/ps/blend/Draw 这些是 <c>[CustomShader*]</c> 段落的属性，不能写在普通 CommandList 里。
    /// 参考实现（Nait3D 菜单）的绘制段就是 <c>[CustomShaderDrawIMG]</c>。
    /// </summary>
    private static string QuadCommandList(string token) => $"CustomShaderJasmQuad_{token}";

    private static string PageCommandList(string token, int page) => $"CommandListJasmPage{page}_{token}";

    private static string RowCommandList(string token, int index) => $"CommandListJasmRow{index}_{token}";

    private static string FooterCommandList(string token) => $"CommandListJasmFooter_{token}";

    /// <summary>token 只保留字母数字，避免用户/路径里的奇怪字符进入 ini 标识符。</summary>
    private static string NormalizeToken(string token)
    {
        var cleaned = new string(token.Where(char.IsLetterOrDigit).ToArray());
        return cleaned.Length == 0 ? "menu" : cleaned.ToLowerInvariant();
    }

    private static string? SanitizeCondition(string condition)
    {
        if (string.IsNullOrWhiteSpace(condition))
            return null;

        var trimmed = condition.Trim();
        if (trimmed.Length > 400)
            return null;

        // 只允许变量引用、比较、逻辑运算与括号；出现其它符号就整条丢弃，宁可少一个门控也不要坏 ini
        foreach (var c in trimmed)
        {
            if (char.IsLetterOrDigit(c) || c is ' ' or '\t' or '$' or '\\' or '_' or '.' or '-' or '"' or '\''
                or '=' or '!' or '<' or '>' or '&' or '|' or '(' or ')')
                continue;

            return null;
        }

        return trimmed;
    }

    private static string SanitizeComment(string text)
        => text.Replace('\r', ' ').Replace('\n', ' ').Replace(';', ':');

    private static string Unquote(string value)
        => value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;
}
