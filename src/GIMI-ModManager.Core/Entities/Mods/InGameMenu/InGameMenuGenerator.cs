using System.Security.Cryptography;
using System.Text;
using GIMI_ModManager.Core.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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

        rows = EnsureAsciiNamespaces(modFolderPath, rows);
        rows = ApplyRowLayout(rows, options ?? new InGameMenuOptions());

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
        // 补过 ASCII namespace 之后，每个 Mod 的 $object_detected 都是独立的，
        // 直接用它做逐角色门控（比 hash 可靠；hash 可能命中共享资源导致叠菜单）。
        var gateNamespace = rows.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.Namespace))?.Namespace;
        var detectionVariable = !string.IsNullOrWhiteSpace(gateNamespace)
                                && ContainsObjectDetected(modFolderPath)
            ? "$\\" + gateNamespace + "\\object_detected"
            : string.Empty;
        var detectionHash = detectionVariable.Length == 0 ? ExtractDetectionHash(modFolderPath) : null;

        effectiveOptions = effectiveOptions with
        {
            Title = Truncate(StripDisabledPrefix(Path.GetFileName(modFolderPath.TrimEnd(Path.DirectorySeparatorChar))), 40),
            DetectionHash = detectionHash ?? string.Empty,
            DetectionVariable = detectionVariable,
            // 有独立门控变量 / hash 时不再退回未限定的全局 $object_detected
            GateOnObjectDetected = detectionHash is null
                                   && detectionVariable.Length == 0
                                   && effectiveOptions.GateOnObjectDetected
                                   && ContainsObjectDetected(modFolderPath),
        };

        var menuToken = BuildToken(Path.GetFileName(modFolderPath.TrimEnd(Path.DirectorySeparatorChar)));
        var build = InGameMenuIniBuilder.Build(rows, menuToken, effectiveOptions);

        try
        {
            if (Directory.Exists(assetsDirectory))
                Directory.Delete(assetsDirectory, recursive: true);
            Directory.CreateDirectory(assetsDirectory);

            foreach (var texture in build.Textures)
                WriteTexture(targetDirectory, texture, effectiveOptions.Theme);

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

        // 还原生成时补进作者 ini 的 ASCII namespace 行
        foreach (var ini in Directory.EnumerateFiles(modFolderPath, "*.ini", SearchOption.AllDirectories))
        {
            try
            {
                IniFileText.TryRemoveAddedNamespace(ini);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return removed;
    }

    /// <summary>菜单是否已在磁盘上（用于 UI 状态与失效检测）。</summary>
    public static bool IsGenerated(string modFolderPath)
        => Directory.Exists(modFolderPath)
           && Directory.EnumerateFiles(modFolderPath, MenuIniFileName, SearchOption.AllDirectories)
               .Any(LooksLikeGeneratedIni);

    /// <summary>
    /// 菜单存在且由<b>当前</b>生成器版本产出。批量补齐用这个判断，而不是简单的文件存在——
    /// 旧版本产出的菜单可能有已知缺陷（v2 的鼠标命中用了会冻结的 <c>cursor_x/y</c>），
    /// 升级后应当自动重建，不能因为「文件已经在了」就永远跳过。
    /// </summary>
    public static bool IsUpToDate(string modFolderPath)
    {
        if (!Directory.Exists(modFolderPath))
            return false;

        foreach (var dir in Directory.EnumerateDirectories(modFolderPath, AssetsDirectoryName, SearchOption.AllDirectories))
        {
            var manifestPath = Path.Combine(dir, ManifestFileName);
            if (!File.Exists(manifestPath))
                continue;

            try
            {
                var version = JObject.Parse(File.ReadAllText(manifestPath))
                    .Value<int?>("generatorVersion") ?? 0;
                return version >= InGameMenuOptions.CurrentGeneratorVersion;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                // 读不出来就当过期：重建比留着一个坏菜单安全
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// 给没有 <c>namespace =</c> 的 Mod 补一行 ASCII namespace。
    ///
    /// 3DMigoto 用 ini 的**路径**当默认命名空间，而 Mod 路径通常含中文；
    /// 菜单里写成 <c>$\Mods\character\绯雪-…\mod.ini\var</c> 会被表达式解析器判为语法错误
    /// （真机日志：<c>Syntax Error ... \绯雪-…\mod.ini\skirt</c>）。补一行 ASCII namespace 后，
    /// 菜单用 <c>$\JasmXXXXXXXX\var</c> 就能正常读写。
    ///
    /// 只改这一个文件的这一行；<see cref="Remove"/> 会按标记把它删掉。
    /// </summary>
    private static IReadOnlyList<ModIniToggleRow> EnsureAsciiNamespaces(
        string modFolderPath,
        IReadOnlyList<ModIniToggleRow> rows)
    {
        var assigned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ModIniToggleRow>(rows.Count);

        foreach (var row in rows)
        {
            if (!string.IsNullOrWhiteSpace(row.Namespace))
            {
                result.Add(row);
                continue;
            }

            if (!assigned.TryGetValue(row.IniRelativePath, out var ns))
            {
                // 必须全局唯一：不同 Mod 的 ini 相对路径常常都是 "mod.ini"，
                // 只用相对路径会让两个 Mod 共用一个命名空间、互相串改变量。
                var seed = (modFolderPath + "|" + row.IniRelativePath).ToLowerInvariant();
                ns = "Jasm" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed)))[..8];

                try
                {
                    IniFileText.TryInsertNamespace(Path.Combine(modFolderPath, row.IniRelativePath), ns);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // 写不进去就退回旧行为：菜单能显示，但点击不会作用到 Mod
                    ns = string.Empty;
                }

                assigned[row.IniRelativePath] = ns;
            }

            result.Add(string.IsNullOrEmpty(ns) ? row : row with { Namespace = ns });
        }

        return result;
    }

    /// <summary>
    /// 应用用户对行的自定义：先按 <see cref="InGameMenuOptions.HiddenRows"/> 过滤，
    /// 再按 <see cref="InGameMenuOptions.RowOrder"/> 排序；未列出的行保持原相对顺序追加。
    /// </summary>
    private static IReadOnlyList<ModIniToggleRow> ApplyRowLayout(
        IReadOnlyList<ModIniToggleRow> rows,
        InGameMenuOptions options)
    {
        if (options.HiddenRows.Count == 0 && options.RowOrder.Count == 0)
            return rows;

        var hidden = new HashSet<string>(options.HiddenRows, StringComparer.OrdinalIgnoreCase);
        var visible = rows.Where(row => !hidden.Contains(row.Key)).ToList();

        if (options.RowOrder.Count == 0)
            return visible;

        // OrderBy 是稳定排序：未出现在 RowOrder 里的行保持原有相对顺序，排在最后。
        var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < options.RowOrder.Count; i++)
            order.TryAdd(options.RowOrder[i], i);

        return visible
            .OrderBy(row => order.TryGetValue(row.Key, out var index) ? index : int.MaxValue)
            .ToList();
    }

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
    /// 提取这个 Mod 用来检测「角色模型正在绘制」的纹理 hash。
    ///
    /// 为什么不用 <c>$object_detected</c>：它在没有 namespace 的 Mod 里是**全局共享**变量，
    /// 多个角色的 Mod 会互相点亮菜单。而每个 Mod 的 <c>[TextureOverrideComponent*]</c> 里
    /// 设置 <c>$object_detected = 1</c> 的那一段带有角色专属的 <c>hash =</c>，
    /// 把同一个 hash 放进我们自己的 TextureOverride，就能做到「只在该角色绘制时点亮」。
    /// </summary>
    public static string? ExtractDetectionHash(string modFolderPath)
    {
        if (!Directory.Exists(modFolderPath))
            return null;

        foreach (var file in Directory
                     .EnumerateFiles(modFolderPath, "*.ini", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(Path.GetFileName(file), MenuIniFileName, StringComparison.OrdinalIgnoreCase))
                continue;

            string text;
            try
            {
                text = IniFileText.ReadAllText(file);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            string? sectionHash = null;

            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();

                if (line.StartsWith('['))
                {
                    sectionHash = null;
                    continue;
                }

                if (line.StartsWith("hash", StringComparison.OrdinalIgnoreCase))
                {
                    var eq = line.IndexOf('=');
                    if (eq > 0)
                        sectionHash = line[(eq + 1)..].Trim();
                    continue;
                }

                if (sectionHash is not null
                    && line.StartsWith("$object_detected", StringComparison.OrdinalIgnoreCase)
                    && line.Replace(" ", string.Empty).Contains("=1", StringComparison.Ordinal))
                {
                    return sectionHash;
                }
            }
        }

        return null;
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

    private static void WriteTexture(string targetDirectory, MenuTextureSpec spec, MenuTheme theme)
    {
        var relative = spec.FileName.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.Combine(targetDirectory, relative);

        // 防御：纹理路径绝不能跑出目标目录
        var normalizedRoot = Path.GetFullPath(targetDirectory) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(fullPath).StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            return;

        var surface = spec.Kind switch
        {
            MenuTextureKind.Panel => MenuTextureFactory.CreatePanel(spec.Width, spec.Height, theme),
            MenuTextureKind.Hover => MenuTextureFactory.CreateHover(spec.Width, spec.Height),
            MenuTextureKind.Marker => MenuTextureFactory.CreateSolid(spec.Width, spec.Height, 255, 40, 40, 230),
            MenuTextureKind.RowBar => MenuTextureFactory.CreateRowBar(spec.Width, spec.Height, active: false, theme),
            MenuTextureKind.RowHover => MenuTextureFactory.CreateRowBar(spec.Width, spec.Height, active: true, theme),
            MenuTextureKind.Tab => MenuTextureFactory.CreateTab(spec.Width, spec.Height, active: false, theme),
            MenuTextureKind.TabActive => MenuTextureFactory.CreateTab(spec.Width, spec.Height, active: true, theme),
            MenuTextureKind.Sidebar => MenuTextureFactory.CreateSidebar(spec.Width, spec.Height, theme),
            MenuTextureKind.Underline => MenuTextureFactory.CreateUnderline(spec.Width, spec.Height, theme),
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
