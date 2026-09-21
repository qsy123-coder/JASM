using GIMI_ModManager.Core.ModMarket;

namespace JASM.Tests;

/// <summary>
/// 引擎单测用的最小行实现。默认值刻意取「快照里最常见的样子」，
/// 每个用例只改它真正关心的那几个属性。
/// </summary>
internal sealed class TestMarketRow : IMarketModRow
{
    public Guid Id { get; init; } = Guid.Empty;

    public string Title { get; init; } = string.Empty;

    public string Character { get; init; } = string.Empty;

    public bool IsPublished { get; init; } = true;

    public bool Nsfw { get; init; }

    public string? DownloadUrl { get; init; }

    public int Views { get; init; }

    public int LikesCount { get; init; }

    public int DownloadsCount { get; init; }

    public DateTime CreatedAt { get; init; } = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
}
