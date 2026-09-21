using System.Text.Json;
using GIMI_ModManager.WinUI.Models;
using Serilog;

namespace GIMI_ModManager.WinUI.Services.ModMarket;

/// <summary>
/// mods 行的反序列化契约。实时路径（PostgREST）与快照降级路径共用同一份
/// <see cref="JsonSerializerOptions"/> 与同一个容错循环 —— 两条路径各自维护一份的话，
/// 迟早会在某个字段上悄悄分叉，而表现只是「降级时某个字段变空」，极难排查。
/// </summary>
internal static class ModMarketJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// 逐元素容错反序列化：单行坏数据只丢那一行，不毁整页。
    /// 日志文案与 <c>DroppedEntries</c> 的内容要保持原样 —— 界面上的调试浮层直接展示它。
    /// </summary>
    /// <param name="root">JSON 数组（PostgREST 结果或快照顶层都是裸数组）。</param>
    /// <returns>成功反序列化的行、原始元素个数、被丢弃元素的可读描述。</returns>
    public static (List<ModMarketMod> Mods, int RawCount, List<string> Dropped) ReadRows(
        JsonElement root,
        ILogger logger)
    {
        var mods = new List<ModMarketMod>();
        var dropped = new List<string>();
        var rawCount = 0;

        foreach (var element in root.EnumerateArray())
        {
            rawCount++;
            try
            {
                var mod = element.Deserialize<ModMarketMod>(Options);
                if (mod != null) mods.Add(mod);
            }
            catch (JsonException jex)
            {
                var raw = element.ToString();
                // Capture the specific error path and a longer snippet for the overlay
                var msg = $"[{jex.Path ?? "(root)"}] {jex.Message}";
                var snippet = raw.Length > 600 ? raw[..600] : raw;
                dropped.Add($"{msg}\n{snippet}");
                logger.Warning(jex, "Failed to deserialize mod entry (#{Index}) at {Path}: {Raw}",
                    rawCount, jex.Path, snippet);
            }
        }

        return (mods, rawCount, dropped);
    }
}
