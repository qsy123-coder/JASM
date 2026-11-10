using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using GIMI_ModManager.Core.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GIMI_ModManager.Core.Entities.Mods.InGameMenu;

public enum InGameMenuGenerationStatus
{
    Generated,
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
public static partial class InGameMenuGenerator
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

        var effectiveOptionsInput = options ?? new InGameMenuOptions();

        // 作者已有可点击菜单时也照常生成：JASM 菜单只在按 F7 时显示，
        // 不与作者常驻菜单争抢；而「[Present] + 光标 + 鼠标键」的检测规则会把
        // 可拖动的帮助图 / 浮层误判成菜单（绯雪 mod 的 draw_image.ini）。
        // 检测结果只用于日志，方便解释「屏幕上为什么可能有两份菜单」。
        var detection = ClickableMenuDetector.Detect(modFolderPath);
        if (detection.HasClickableMenu)
        {
            Serilog.Log.Information(
                "[InGameMenu] 检测到作者可点击菜单，仍生成 JASM 菜单：{Mod}（{File}）",
                Path.GetFileName(modFolderPath.TrimEnd(Path.DirectorySeparatorChar)),
                detection.MatchedFile);
        }

        // 作者 ini 的 [Key*] 行；油光本体自带的三行按键不参与菜单
        IReadOnlyList<ModIniToggleRow> parsedRows = ModIniToggleParser.ParseDirectory(modFolderPath)
            .Where(r => string.IsNullOrWhiteSpace(r.Namespace)
                        || !effectiveOptionsInput.ExcludedNamespaces.Contains(r.Namespace, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (parsedRows.Count == 0 && effectiveOptionsInput.ExtraRows.Count == 0)
            return new InGameMenuGenerationResult(
                InGameMenuGenerationStatus.SkippedNoRows,
                null, null, 0, 0,
                "没有解析到任何 [Key*] 段落");

        parsedRows = EnsureAsciiNamespaces(modFolderPath, parsedRows);
        parsedRows = ApplyRowLayout(parsedRows, effectiveOptionsInput);

        // 注入行固定置顶；它们不参与行布局，也不改变「菜单落在哪个目录」的判断
        var rows = effectiveOptionsInput.ExtraRows.Count > 0
            ? effectiveOptionsInput.ExtraRows.Concat(parsedRows).ToList()
            : parsedRows;

        var clickable = rows.Count(r => r.IsClickable);
        if (clickable == 0)
            return new InGameMenuGenerationResult(
                InGameMenuGenerationStatus.SkippedNoClickableRows,
                null, null, rows.Count, 0,
                "没有可直接切换的循环变量（[Key*] 里缺少 $变量 = 多值）");

        // 放到「拥有最多行」的那个 ini 目录：3DMigoto 的 include_recursive 会加载它，
        // 且纹理相对路径以 ini 所在目录为准。只用作者行判断，避免注入行把目录带偏。
        var targetDirectory = PickTargetDirectory(modFolderPath, parsedRows);
        var iniPath = Path.Combine(targetDirectory, MenuIniFileName);
        var assetsDirectory = Path.Combine(targetDirectory, AssetsDirectoryName);

        if (!overwriteExisting && File.Exists(iniPath))
            return Failed("菜单已存在且不允许覆盖");

        // 安全闸：同名文件存在但不是 JASM 生成的，绝不覆盖
        if (File.Exists(iniPath) && !LooksLikeGeneratedIni(iniPath))
            return Failed($"{MenuIniFileName} 已存在且不是 JASM 生成的文件，拒绝覆盖");

        var effectiveOptions = options ?? new InGameMenuOptions();
        // 逐角色门控：收集所有真正设置 $object_detected = 1 的文件所属命名空间。
        // 作者常把 [Key*] 与 $object_detected 放在不同 ini（甚至普通 / 大招两套形态）里，
        // 只取「第一条可点击行」的命名空间会得到一个永远为 0 的条件 —— 菜单根本不显示。
        var detectionVariables = CollectDetectionVariables(modFolderPath);
        // 纹理 hash 只作为**兜底**：拿不到 per-character 变量时才用。
        // 它命中的是网格资源，在游戏暂停 / 时间冻结时会让上一件 Mod 一直「在场」，
        // 造成两个角色的菜单同时出现（女漂 + 椿 实机复现）。变量门控本身按角色隔离，
        // 有变量时必须只信变量。
        var detectionHashes = detectionVariables.Count == 0
            ? CollectDetectionHashes(modFolderPath)
            : [];

        // 每个 ini 文件所属形态的门控变量：多形态 Mod 里用来置灰/禁用非当前形态的行
        var rowDetectionVariables = CollectRowDetectionVariables(modFolderPath);

        effectiveOptions = effectiveOptions with
        {
            RowDetectionVariables = rowDetectionVariables,
            Title = Truncate(StripDisabledPrefix(Path.GetFileName(modFolderPath.TrimEnd(Path.DirectorySeparatorChar))), 40),
            DetectionHash = detectionHashes.FirstOrDefault() ?? string.Empty,
            DetectionHashes = detectionHashes,
            DetectionVariable = string.Empty,
            DetectionVariables = detectionVariables,
            // 有独立门控变量 / hash 时不再退回未限定的全局 $object_detected
            GateOnObjectDetected = detectionHashes.Count == 0
                                   && detectionVariables.Count == 0
                                   && effectiveOptions.GateOnObjectDetected
                                   && ContainsObjectDetected(modFolderPath),
        };

        // 参考素材主题：直接拷贝参考 Mod 的通用 PNG（按钮/页签），其余仍由 JASM 生成。
        // 缺文件就跳过该资源，退回到同主题的自绘 .dds。
        var pngAssets = ResolveReferenceAssets(effectiveOptions);
        if (pngAssets.Count > 0)
        {
            effectiveOptions = effectiveOptions with
            {
                PngResources = ReferenceAssetMap
                    .Where(mapping => pngAssets.ContainsKey(mapping.Kind))
                    .Select(mapping => mapping.ResourceName)
                    .ToArray(),
            };
        }

        var menuToken = BuildToken(Path.GetFileName(modFolderPath.TrimEnd(Path.DirectorySeparatorChar)));
        var build = InGameMenuIniBuilder.Build(rows, menuToken, effectiveOptions);

        try
        {
            // 写完必须自检：曾出现过「纹理一张没写、ini/清单照写、还报已生成」的静默失败，
            // 表现为游戏里 F7 有按键脉冲但菜单不显示。缺任何一张就整体重试一次；
            // 仍不完整就报失败，绝不留下指向不存在纹理的菜单。
            var written = WriteAssets(assetsDirectory, build.Textures, pngAssets, targetDirectory,
                effectiveOptions);
            if (written < build.Textures.Count)
            {
                Serilog.Log.Warning("[InGameMenu] 纹理写入不完整（{Written}/{Expected}），重试：{Dir}",
                    written, build.Textures.Count, assetsDirectory);
                written = WriteAssets(assetsDirectory, build.Textures, pngAssets, targetDirectory,
                    effectiveOptions);
            }

            if (written < build.Textures.Count)
            {
                TryCleanup(iniPath, assetsDirectory);
                return Failed($"菜单纹理写入不完整（期望 {build.Textures.Count}，实际 {written}）：{assetsDirectory}");
            }

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
        if (rows.Count == 0)
            return modFolderPath;

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
    /// 收集这个 Mod 里所有真正把 <c>$object_detected</c> 置 1 的文件的门控变量。
    ///
    /// 普通 / 大招两套形态各自有一个 <c>$object_detected</c>，因此可能返回多个限定名；
    /// 菜单用 <c>||</c> 串联，任意一套形态在绘制时都能点亮菜单。
    /// 文件本身没有 namespace 时变量属于全局命名空间，返回未限定的 <c>$object_detected</c>。
    /// </summary>
    private static IReadOnlyList<string> CollectDetectionVariables(string modFolderPath)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory
                     .EnumerateFiles(modFolderPath, "*.ini", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(Path.GetFileName(file), MenuIniFileName, StringComparison.OrdinalIgnoreCase))
                continue;

            // 3DMigoto 不加载 DISABLED_ 目录 / 文件：里面的 $object_detected 永远不会被置 1，
            // 拿它做门控会让菜单永远不显示
            if (InGameMenuPathFilter.IsDisabled(modFolderPath, file))
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

            var matches = ObjectDetectedAssignment().Matches(text);
            if (matches.Count == 0)
                continue;

            var fileNamespace = ReadFileNamespace(text);

            foreach (Match match in matches)
            {
                // 赋值本身带限定名时以它为准，否则用文件级 namespace
                var qualifier = match.Groups[1].Value.Trim();
                if (qualifier.Length == 0)
                    qualifier = fileNamespace;

                var variable = qualifier.Length == 0
                    ? "$object_detected"
                    : "$\\" + qualifier + "\\object_detected";

                if (seen.Add(variable))
                    result.Add(variable);
            }
        }

        // 全局 $object_detected 是所有 Mod 共享的：只要还有任何一个**命名空间**变量，
        // 就不能把它混进门控 —— 否则别的角色把全局量置 1 时，这份菜单也会亮
        //（女漂 + 椿 同时出现就是这个原因）。
        if (result.Any(v => v.StartsWith("$\\", StringComparison.Ordinal)))
            result.RemoveAll(v => v == "$object_detected");

        return result;
    }

    /// <summary>
    /// 收集「每个 ini 文件 → 它所属形态的门控变量」。只在文件确实设置
    /// <c>$object_detected = 1</c> 且带命名空间时记录；全局变量无法区分形态，不参与。
    /// 键是相对 Mod 根目录的 ini 路径（与 <see cref="ModIniToggleRow.IniRelativePath"/> 一致）。
    /// </summary>
    private static IReadOnlyDictionary<string, string> CollectRowDetectionVariables(string modFolderPath)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory
                     .EnumerateFiles(modFolderPath, "*.ini", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(Path.GetFileName(file), MenuIniFileName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (InGameMenuPathFilter.IsDisabled(modFolderPath, file))
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

            if (!ObjectDetectedAssignment().IsMatch(text))
                continue;

            var ns = ReadFileNamespace(text);
            if (string.IsNullOrWhiteSpace(ns))
                continue; // 全局 $object_detected 是所有 Mod 共享的，不能区分形态

            result[Path.GetRelativePath(modFolderPath, file)] = "$\\" + ns + "\\object_detected";
        }

        return result;
    }

    /// <summary>读取文件级 <c>namespace = X</c>；没有则返回空串（变量属于全局命名空间）。</summary>
    private static string ReadFileNamespace(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '/')
                continue;

            if (!line.StartsWith("namespace", StringComparison.OrdinalIgnoreCase))
                continue;

            var eq = line.IndexOf('=');
            if (eq > 0)
                return line[(eq + 1)..].Trim().Trim('"');
        }

        return string.Empty;
    }

    /// <summary>
    /// 匹配 <c>$object_detected = 1</c> / <c>$\Ns\object_detected = 1</c>（含 post / 行内注释），
    /// 捕获其中可选的命名空间。只认赋值为 1 的行，<c>global $object_detected = 0</c> 不算。
    /// </summary>
    [GeneratedRegex(@"^\s*(?:post\s+)?\$(?:([^=\s\\]+)\\)?object_detected\s*=\s*1(?:\.0*)?\s*(?:;.*)?$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex ObjectDetectedAssignment();

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

            // 3DMigoto 不加载 DISABLED_ 目录 / 文件：里面的 $object_detected 永远不会被置 1，
            // 拿它做门控会让菜单永远不显示
            if (InGameMenuPathFilter.IsDisabled(modFolderPath, file))
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
    /// 收集所有「设置 <c>$object_detected = 1</c> 的 TextureOverride 段」的纹理 hash。
    /// 多套形态（普通 / 大招）会得到多个 hash，菜单对每个 hash 生成一个检测段，
    /// 任意一个命中就刷新 <c>last_seen</c> —— 完全不依赖变量命名空间。
    /// </summary>
    private static IReadOnlyList<string> CollectDetectionHashes(string modFolderPath)
    {
        const int maxHashes = 32; // 防御：异常 Mod 可能有大量同义段
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory
                     .EnumerateFiles(modFolderPath, "*.ini", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(Path.GetFileName(file), MenuIniFileName, StringComparison.OrdinalIgnoreCase))
                continue;

            // 3DMigoto 不加载 DISABLED_ 目录 / 文件：里面的 $object_detected 永远不会被置 1，
            // 拿它做门控会让菜单永远不显示
            if (InGameMenuPathFilter.IsDisabled(modFolderPath, file))
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

                if (sectionHash is not null && ObjectDetectedAssignment().IsMatch(line))
                {
                    if (seen.Add(sectionHash))
                    {
                        result.Add(sectionHash);
                        if (result.Count >= maxHashes)
                            return result;
                    }
                }
            }
        }

        return result;
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

            // 3DMigoto 不加载 DISABLED_ 目录 / 文件：里面的 $object_detected 永远不会被置 1，
            // 拿它做门控会让菜单永远不显示
            if (InGameMenuPathFilter.IsDisabled(modFolderPath, file))
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

    /// <summary>参考素材：通用的按钮/页签 PNG（Header/Footer 带作者标题，不复用）。</summary>
    private static readonly (MenuTextureKind Kind, string Source, string ResourceName)[] ReferenceAssetMap =
    [
        (MenuTextureKind.RowBar, "ButtonBg.png", "Row"),
        (MenuTextureKind.RowHover, "ButtonBorder.png", "RowHover"),
        (MenuTextureKind.Tab, "ui2_TabBg.png", "Tab"),
        (MenuTextureKind.TabActive, "ui2_TabBgSelected.png", "TabActive"),
    ];

    /// <summary>找出参考素材目录里实际存在的 PNG；缺的会退回自绘纹理。</summary>
    private static IReadOnlyDictionary<MenuTextureKind, string> ResolveReferenceAssets(InGameMenuOptions options)
    {
        var result = new Dictionary<MenuTextureKind, string>();
        if (!options.Theme.UseReferenceAssets || string.IsNullOrWhiteSpace(options.ReferenceAssetsDirectory))
            return result;

        foreach (var (kind, source, _) in ReferenceAssetMap)
        {
            var path = Path.Combine(options.ReferenceAssetsDirectory, source);
            if (File.Exists(path))
                result[kind] = path;
        }

        return result;
    }

    /// <summary>
    /// 新建资产目录并写入全部纹理，返回实际落盘的纹理数（供调用方自检）。
    /// 抽出来是为了「不完整就整体重试一次」——单独重写失败的几张会留下半新半旧的状态。
    /// </summary>
    private static int WriteAssets(
        string assetsDirectory,
        IReadOnlyList<MenuTextureSpec> textures,
        IReadOnlyDictionary<MenuTextureKind, string> pngAssets,
        string targetDirectory,
        InGameMenuOptions options)
    {
        if (Directory.Exists(assetsDirectory))
            Directory.Delete(assetsDirectory, recursive: true);
        Directory.CreateDirectory(assetsDirectory);

        // 随包分发的字体（HYWenHei-85W）进程私有加载；缺文件时退回系统雅黑
        GdiTextRasterizer.UseFont(options.FontFilePath, options.FontFamily);

        var written = 0;
        foreach (var texture in textures)
        {
            string targetPath;
            if (pngAssets.TryGetValue(texture.Kind, out var sourcePath))
            {
                targetPath = Path.Combine(assetsDirectory,
                    Path.GetFileNameWithoutExtension(texture.FileName) + ".png");
                File.Copy(sourcePath, targetPath, overwrite: true);
            }
            else
            {
                targetPath = Path.Combine(targetDirectory,
                    texture.FileName.Replace('/', Path.DirectorySeparatorChar));
                WriteTexture(targetDirectory, texture, options.Theme);
            }

            if (File.Exists(targetPath))
                written++;
            else
                Serilog.Log.Warning("[InGameMenu] 纹理未落盘：{Path}", targetPath);
        }

        return written;
    }

    private static void WriteTexture(string targetDirectory, MenuTextureSpec spec, MenuTheme theme)
    {
        var relative = spec.FileName.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.Combine(targetDirectory, relative);

        // 防御：纹理路径绝不能跑出目标目录。
        // 根路径先去掉尾部斜杠再补一个，避免 targetDirectory 带尾斜杠时拼出 "...\\" 而误判越界
        // （误判的后果是整份菜单的纹理被静默跳过）。
        var normalizedRoot = Path.GetFullPath(targetDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(fullPath).StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            Serilog.Log.Warning("[InGameMenu] 纹理路径越界，已跳过：{Path}（根：{Root}）", fullPath, normalizedRoot);
            return;
        }

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
                rawLabel = r.RawLabel,
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