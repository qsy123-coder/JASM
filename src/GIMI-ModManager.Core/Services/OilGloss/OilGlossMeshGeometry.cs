namespace GIMI_ModManager.Core.Services.OilGloss;

/// <summary>
/// 读取 WWMI 导出的 <c>Meshes/Position.buf</c> 与 <c>Index.buf</c>，
/// 估算每块 drawcall 在角色高度上的覆盖范围。
///
/// WWMI 模型来自 Unreal（Z 轴向上）：0 = 脚底，1 = 头顶。
/// 有了这个范围就能分清「身体（脚→躯干）」和「头/发/配饰」，
/// 不用再靠“索引数最大”去猜。任何一步对不上（没有 Meshes、格式不认识）就返回 null，
/// 调用方退回原来的启发式，绝不因为解析失败而报错。
/// </summary>
internal static class OilGlossMeshGeometry
{
    /// <summary>一块网格在角色高度上的归一化范围（0 = 脚底，1 = 头顶）。</summary>
    public sealed record HeightRange(double Min, double Max);

    public static IReadOnlyDictionary<(string IniPath, int StartIndex, int IndexCount), HeightRange>? TryAnalyze(
        string modFolderPath,
        IReadOnlyList<OilGlossDrawcall> drawcalls)
    {
        if (drawcalls.Count == 0)
            return null;

        try
        {
            return Analyze(modFolderPath, drawcalls);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static Dictionary<(string IniPath, int StartIndex, int IndexCount), HeightRange>? Analyze(
        string modFolderPath,
        IReadOnlyList<OilGlossDrawcall> drawcalls)
    {
        var meshesDirectory = FindDirectory(modFolderPath, "Meshes");
        if (meshesDirectory is null)
            return null;

        var positionPath = FindFile(meshesDirectory, "Position.buf");
        var indexPath = FindFile(meshesDirectory, "Index.buf");
        if (positionPath is null || indexPath is null)
            return null;

        // WWMI 的 Position.buf 只存位置：每个顶点 3 个 float。
        var positions = File.ReadAllBytes(positionPath);
        if (positions.Length == 0 || positions.Length % 12 != 0)
            return null;

        var vertexCount = positions.Length / 12;
        var indices = File.ReadAllBytes(indexPath);

        // 用「所有 drawcall 覆盖到的最大索引」反推索引宽度（16 位或 32 位）。
        long maxEnd = 0;
        foreach (var draw in drawcalls)
            maxEnd = Math.Max(maxEnd, (long)draw.StartIndex + draw.IndexCount);

        var elementSize = indices.Length == maxEnd * 4 ? 4
            : indices.Length == maxEnd * 2 ? 2
            : 0;

        if (maxEnd <= 0 || elementSize == 0)
            return null;

        var ranges = new List<(OilGlossDrawcall Draw, double Min, double Max)>(drawcalls.Count);
        foreach (var draw in drawcalls)
        {
            if (draw.IndexCount <= 0)
                continue;

            var min = double.MaxValue;
            var max = double.MinValue;
            var used = 0;

            for (var k = 0; k < draw.IndexCount; k++)
            {
                var offset = ((long)draw.StartIndex + k) * elementSize;
                if (offset + elementSize > indices.Length)
                    break;

                var vertexIndex = elementSize == 4
                    ? BitConverter.ToUInt32(indices, (int)offset)
                    : BitConverter.ToUInt16(indices, (int)offset);

                if (vertexIndex >= vertexCount)
                    continue;

                var z = BitConverter.ToSingle(positions, (int)vertexIndex * 12 + 8);
                if (z < min)
                    min = z;
                if (z > max)
                    max = z;

                used++;
            }

            if (used > 0)
                ranges.Add((draw, min, max));
        }

        if (ranges.Count == 0)
            return null;

        var globalMin = ranges.Min(r => r.Min);
        var globalMax = ranges.Max(r => r.Max);
        var span = globalMax - globalMin;
        if (span <= 0)
            return null;

        return ranges.ToDictionary(
            r => (r.Draw.IniRelativePath, r.Draw.StartIndex, r.Draw.IndexCount),
            r => new HeightRange((r.Min - globalMin) / span, (r.Max - globalMin) / span));
    }

    private static string? FindDirectory(string root, string name)
    {
        try
        {
            return Directory
                .EnumerateDirectories(root, name, SearchOption.AllDirectories)
                .OrderBy(path => path.Length)
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? FindFile(string directory, string name)
        => Directory
            .EnumerateFiles(directory, name, SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
}
