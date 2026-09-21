using GIMI_ModManager.WinUI.Models;

namespace GIMI_ModManager.WinUI.Services.ModMarket;

/// <summary>
/// 一份已解压、已反序列化好的兜底快照，以及它生成的时间。
///
/// ⚠️ 刻意不实现 IDisposable：行里的 <c>drive_links</c> 是 <c>JsonElement?</c>，
/// 它只是 JsonDocument 缓冲区上的一层视图。一旦把那份文档 Dispose 掉，
/// 之后任何一次取网盘链接都会 ObjectDisposedException —— 而详情面板是惰性取值的，
/// 释放时机与取值时机根本没法对齐。不释放的代价只是缓冲池少回收一次。
/// </summary>
public sealed record ModMarketSnapshot(
    IReadOnlyList<ModMarketMod> Mods,
    DateTimeOffset GeneratedAt,
    int DroppedEntries);
