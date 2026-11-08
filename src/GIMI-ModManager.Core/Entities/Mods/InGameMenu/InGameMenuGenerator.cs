using System.Security.Cryptography;
using System.Text;
using GIMI_ModManager.Core.Helpers;
using Newtonsoft.Json;

namespace GIMI_ModManager.Core.Entities.Mods.InGameMenu;

public enum InGameMenuGenerationStatus
{
    Generated,
    SkippedExistingClickableMenu,
    SkippedNoRows,
    SkippedNoClickableRows,
    /// <summary>同一角色同时启用了多件 Mod：按「只给当前生效的那一件」规则跳过。</summary>
    SkippedMultipleEnabledMods,
    /// <summary>菜单已经生成过（批量补齐不会重复做纹理生成）。</summary>
    SkippedAlreadyGenerated,
    /// <summary>
    /// Mod 没有 <c>$object_detected</c>（常驻前置/后处理类）：菜单无法按角色门控，
    /// 会一直显示并与角色菜单叠加，批量补齐跳过。
    /// </summary>
    SkippedNoObjectDetected,
    Failed,
}

public sealed record InGameMenuGenerationResult(
    InGameMenuGenerationStatus Status,
    string? IniPath,
    string? AssetsDirectory,
    int RowCount,
    int ClickableRowCount,
    string Reason);

/// <summary>
/// 为一个 Mod 生成/移除游戏内菜单。
///
/// 产物只有两处：
///   • <c>&lt;ini 所在目录&gt;/JASM_Menu.ini</c>
///   • <c>&lt;ini 所在目录&gt;/JASM_Menu/</c>（纹理、shader、manifest）
/// 作者原有文件一个字节都不改，删除这两个路径即可完全回滚。
/// </summary>
public static class InGameMenuGenerator
{
    public const string MenuIniFileName = "JASM_Menu.ini";
    public const string AssetsDirectoryName = "JASM_Menu";
    private const string ManifestFileName = "manifest.json";
    private const string GeneratedHeaderMarker = "; JASM 游戏内切换菜单（自动生成，请勿手改）";

    /// <summary>随菜单生成的 2D 绘制 shader。自研，不依赖作者 Mod 是否带 shader。</summary>
    private const string DrawShader = """
        // JASM 2D quad shader (generated with the menu)
        // IniParams[87] = xy: quad size (normalized), zw: quad offset (normalized)
        // IniParams[88] = tint RGBA
        Texture1D<float4> IniParams : register(t120);

        #define QuadSize IniParams[87].xy
        #define QuadPos  IniParams[87].zw
        #define TintColor IniParams[88]

        struct VertToFrag
        {
            float4 clipPos  : SV_Position0;
            float2 texCoord : TEXCOORD1;
            float4 tint     : COLOR;
        };

        #ifdef VERTEX_SHADER
        void main(out VertToFrag o, uint vid : SV_VertexID)
        {
            float cx = 1.0 - (float)((vid >> 1) & 1);
            float cy = (float)(vid & 1);

            float2 origin;
            origin.x =  QuadPos.x * 2.0 - 1.0;
            origin.y = -QuadPos.y * 2.0 + 1.0;

            float2 extent = float2(QuadSize.x * 2.0, -QuadSize.y * 2.0);

            o.clipPos.xy = origin + extent * float2(cx, cy);
            o.clipPos.zw = float2(0.0, 1.0);
            o.texCoord   = float2(cx, cy);
            o.tint       = TintColor;
        }
        #endif

        #ifdef PIXEL_SHADER
        Texture2D<float4> diffuseTex : register(t100);
        SamplerState LinearSampler
        {
            Filter   = MIN_MAG_MIP_LINEAR;
            AddressU = Clamp;
            AddressV = Clamp;
        };

        void main(VertToFrag i, out float4 o : SV_Target0)
        {
            float4 s = diffuseTex.Sample(LinearSampler, i.texCoord);
            o = s * i.tint;
        }
        #endif
        """;

    /// <summary>
    /// 生成菜单。调用方负责判断「当前生效的 Mod」；这里只做文件与内容层面的事。
    /// </summary>
    public static InGameMenuGenerationResult Generate(
        string modFolderPath,
        InGameMenuOptions? options = null,
        bool overwriteExisting = true)
    {
        if (!Directory.Exists(modFolderPath))
            return Failed("Mod 目录不存在");

        var detection = ClickableMenuDetector.Detect(modFolderPath);
        if (detection.HasClickableMenu)
        {
            return new InGameMenuGenerationResult(
                InGameMenuGenerationStatus.SkippedExistingClickableMenu,
                null, null, 0, 0,
                $"作者已有可点击菜单：{detection.MatchedFile}");
        }

        var rows = ModIniToggleParser.ParseDirectory(modFolderPath);
        if (rows.Count == 0)
            return new InGameMenuGenerationResult(
                InGameMenuGenerationStatus.SkippedNoRows,
                null, null, 0, 0,
                "没有解析到任何 [Key*] 段落");

        var clickable = rows.Count(r => r.IsClickable);
        if (clickable == 0)
            return new InGameMenuGenerationResult(
                InGameMenuGenerationStatus.SkippedNoClickableRows,
                null, null, rows.Count, 0,
                "没有可直接切换的循环变量（[Key*] 里缺少 $变量 = 多值）");

        // 放到「拥有最多行」的那个 ini 目录：3DMigoto 的 include_recursive 会加载它，
        // 且纹理相对路径以 ini 所在目录为准。
        var targetDirectory = PickTargetDirectory(modFolderPath, rows);
        var iniPath = Path.Combine(targetDirectory, MenuIniFileName);
        var assetsDirectory = Path.Combine(targetDirectory, AssetsDirectoryName);

        if (!overwriteExisting && File.Exists(iniPath))
            return Failed("菜单已存在且不允许覆盖");

        // 安全闸：同名文件存在但不是 JASM 生成的，绝不覆盖
        if (File.Exists(iniPath) && !LooksLikeGeneratedIni(iniPath))
            return Failed($"{MenuIniFileName} 已存在且不是 JASM 生成的文件，拒绝覆盖");

        var effectiveOptions = options ?? new InGameMenuOptions();
        effectiveOptions = effectiveOptions with
        {
            Title = Truncate(StripDisabledPrefix(Path.GetFileName(modFolderPath.TrimEnd(Path.DirectorySeparatorChar))), 40),
            GateOnObjectDetected = effectiveOptions.GateOnObjectDetected && ContainsObjectDetected(modFolderPath),
        };

        var menuToken = BuildToken(Path.GetFileName(modFolderPath.TrimEnd(Path.DirectorySeparatorChar)));
        var build = InGameMenuIniBuilder.Build(rows, menuToken, effectiveOptions);

        try
        {
            if (Directory.Exists(assetsDirectory))
                Directory.Delete(assetsDirectory, recursive: true);
            Directory.CreateDirectory(assetsDirectory);

            foreach (var texture in build.Textures)
                WriteTexture(targetDirectory, texture);

            var shaderPath = Path.Combine(assetsDirectory, "draw_2d.hlsl");
            File.WriteAllText(shaderPath, DrawShader, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            WriteManifest(assetsDirectory, modFolderPath, rows, effectiveOptions, menuToken, build);

            // ini 最后写：即使中途失败，也不会留下一个指向不存在纹理的菜单
            File.WriteAllText(iniPath, build.IniText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            return new InGameMenuGenerationResult(
                InGameMenuGenerationStatus.Generated,
                iniPath, assetsDirectory, rows.Count, clickable,
                $"已生成 {clickable} 个可点击行 / 共 {rows.Count} 行");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 失败时尽量清掉半成品，避免游戏加载到残缺菜单
            TryCleanup(iniPath, assetsDirectory);
            return Failed(ex.Message);
        }
    }

    /// <summary>移除 JASM 生成的菜单。只删自己认得的产物。</summary>
    public static bool Remove(string modFolderPath)
    {
        if (!Directory.Exists(modFolderPath))
            return false;

        var removed = false;

        foreach (var ini in Directory.EnumerateFiles(modFolderPath, MenuIniFileName, SearchOption.AllDirectories))
        {
            if (!LooksLikeGeneratedIni(ini))
                continue;

            File.Delete(ini);
            removed = true;
        }

        foreach (var dir in Directory.EnumerateDirectories(modFolderPath, AssetsDirectoryName, SearchOption.AllDirectories))
        {
            if (!File.Exists(Path.Combine(dir, ManifestFileName)))
                continue;

            Directory.Delete(dir, recursive: true);
            removed = true;
        }

        return removed;
    }

    /// <summary>菜单是否已在磁盘上（用于 UI 状态与失效检测）。</summary>
    public static bool IsGenerated(string modFolderPath)
        => Directory.Exists(modFolderPath)
           && Directory.EnumerateFiles(modFolderPath, MenuIniFileName, SearchOption.AllDirectories)
               .Any(LooksLikeGeneratedIni);

    private static bool LooksLikeGeneratedIni(string path)
    {
        try
        {
            using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            // 头两行内必须出现生成标记，避免误删同名文件
            for (var i = 0; i < 3 && reader.ReadLine() is { } line; i++)
            {
                if (line.Contains(GeneratedHeaderMarker, StringComparison.Ordinal))
                    return true;
            }
        }
        catch (IOException)
        {
        }

        return false;
    }

    private static string PickTargetDirectory(string modFolderPath, IReadOnlyList<ModIniToggleRow> rows)
    {
        var best = rows
            .Select(r => Path.GetDirectoryName(r.IniRelativePath) ?? string.Empty)
            .GroupBy(dir => dir, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .First()
            .Key;

        return Path.Combine(modFolderPath, best);
    }

    /// <summary>
    /// Mod 是否提供 <c>$object_detected</c>。没有它说明是常驻前置/后处理类 Mod，
    /// 菜单无法按「当前角色」门控，批量补齐应跳过。
    /// </summary>
    public static bool HasObjectDetectedVariable(string modFolderPath) => ContainsObjectDetected(modFolderPath);

    private static bool ContainsObjectDetected(string modFolderPath)
    {
        foreach (var file in Directory.EnumerateFiles(modFolderPath, "*.ini", SearchOption.AllDirectories))
        {
            if (string.Equals(Path.GetFileName(file), MenuIniFileName, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                if (IniFileText.ReadAllText(file).Contains("object_detected", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return false;
    }

    private static void WriteTexture(string targetDirectory, MenuTextureSpec spec)
    {
        var relative = spec.FileName.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.Combine(targetDirectory, relative);

        // 防御：纹理路径绝不能跑出目标目录
        var normalizedRoot = Path.GetFullPath(targetDirectory) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(fullPath).StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            return;

        var surface = spec.Kind switch
        {
            MenuTextureKind.Panel => MenuTextureFactory.CreatePanel(spec.Width, spec.Height),
            MenuTextureKind.Hover => MenuTextureFactory.CreateHover(spec.Width, spec.Height),
            MenuTextureKind.Marker => MenuTextureFactory.CreateSolid(spec.Width, spec.Height, 255, 40, 40, 230),
            MenuTextureKind.RowBar => MenuTextureFactory.CreateRowBar(spec.Width, spec.Height, active: false),
            MenuTextureKind.RowHover => MenuTextureFactory.CreateRowBar(spec.Width, spec.Height, active: true),
            MenuTextureKind.Tab => MenuTextureFactory.CreateTab(spec.Width, spec.Height, active: false),
            MenuTextureKind.TabActive => MenuTextureFactory.CreateTab(spec.Width, spec.Height, active: true),
            MenuTextureKind.Sidebar => MenuTextureFactory.CreateSidebar(spec.Width, spec.Height),
            MenuTextureKind.Underline => MenuTextureFactory.CreateUnderline(spec.Width, spec.Height),
            _ => GdiTextRasterizer.RenderText(spec.Text, spec.Width, spec.Height, spec.FontSize, spec.Bold, spec.Align),
        };

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var dds = DdsWriter.CreateBgra32(surface.Width, surface.Height, surface.Pixels);
        File.WriteAllBytes(fullPath, dds);
    }

    private static void WriteManifest(
        string assetsDirectory,
        string modFolderPath,
        IReadOnlyList<ModIniToggleRow> rows,
        InGameMenuOptions options,
        string menuToken,
        InGameMenuBuildResult build)
    {
        var manifest = new
        {
            menuVersion = 1,
            generatorVersion = options.GeneratorVersion,
            menuToken,
            openKey = options.OpenKeyTokens,
            createdAtUtc = DateTimeOffset.UtcNow,
            modFolder = Path.GetFileName(modFolderPath.TrimEnd(Path.DirectorySeparatorChar)),
            pageCount = build.PageCount,
            panelWidth = build.PanelWidth,
            panelHeight = build.PanelHeight,
            rows = rows.Select(r => new
            {
                section = r.SectionName,
                ini = r.IniRelativePath,
                variable = r.QualifiedVariable,
                values = r.Values,
                type = r.KeyType,
                key = r.KeyDisplay,
                label = r.Label,
                clickable = r.IsClickable,
            }),
        };

        var json = JsonConvert.SerializeObject(manifest, Newtonsoft.Json.Formatting.Indented);
        File.WriteAllText(Path.Combine(assetsDirectory, ManifestFileName), json,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void TryCleanup(string iniPath, string assetsDirectory)
    {
        try
        {
            if (File.Exists(iniPath))
                File.Delete(iniPath);
            if (Directory.Exists(assetsDirectory))
                Directory.Delete(assetsDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>稳定 token：同名 Mod 无论移动到哪个角色目录都得到同一个后缀（菜单文件不声明 namespace，靠它隔离）。</summary>
    private static string BuildToken(string modFolderName)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(modFolderName.Trim().ToUpperInvariant()));
        return Convert.ToHexString(hash)[..8].ToLowerInvariant();
    }

    private static string StripDisabledPrefix(string name)
        => ModFolderHelpers.GetFolderNameWithoutDisabledPrefix(name);

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max];

    private static InGameMenuGenerationResult Failed(string reason)
        => new(InGameMenuGenerationStatus.Failed, null, null, 0, 0, reason);
}
