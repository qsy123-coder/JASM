using GIMI_ModManager.Core.ModStore;
using GIMI_ModManager.Core.Services.GameBanana.ApiModels;
using GIMI_ModManager.Core.Services.GameBanana.Models;

namespace GIMI_ModManager.Core.Services.GameBanana;

public interface IApiGameBananaClient
{
    /// <summary>
    /// Checks if the GameBanana API is reachable.
    /// </summary>
    public Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the mod profile from the GameBanana API.
    /// </summary>
    /// <param name="modId">The Game banana's mod Id</param>
    /// <param name="cancellationToken"></param>
    /// <returns>ApiModProfile if mod exists or null</returns>
    public Task<ApiModProfile?> GetModProfileAsync(GbModId modId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 取游戏板块的内容流（Mod 商店的「浏览」列表）。
    /// </summary>
    /// <param name="gameId">GameBanana 板块 Id（鸣潮 = 20357）</param>
    /// <param name="sort">
    /// 排序。API 参数名是 <c>_sSort</c>；分页参数是 <c>_nPage</c>，
    /// 页大小**固定 15 条**（<c>perPage</c> 那一族实测被忽略）。
    /// </param>
    /// <param name="page">页码，从 1 开始（<c>_nPage=0</c> 会拿到空结果）</param>
    /// <returns>解析后的一页；请求/反序列化失败返回 null（空态交给 UI，不把异常抛进调用方）</returns>
    public Task<ModStorePage?> GetGameSubfeedAsync(GbGameId gameId, GbSubfeedSort sort, int page,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 在板块内搜索（Mod 商店的「搜索」列表）。
    ///
    /// ⚠️ 返回的是**混合类型**提交（实测同一页里混着 Mod / Question / Request / Poll …），
    /// <see cref="ModStorePage.FromApi"/> 会把非 Mod 的滤掉 —— 所以一页 15 条搜出来可能只剩几条，
    /// 判断「还有没有下一页」要看 <see cref="ModStorePage.HasMore"/> 而不是条目数。
    /// </summary>
    /// <param name="gameId">GameBanana 板块 Id</param>
    /// <param name="searchQuery">搜索词，不能为空</param>
    /// <param name="page">页码，从 1 开始（<c>_nPage</c>）</param>
    public Task<ModStorePage?> SearchGameModsAsync(GbGameId gameId, string searchQuery, int page,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the mod files info from the GameBanana API.
    /// </summary>
    /// <param name="modId">The Game banana's mod Id</param>
    /// <param name="cancellationToken"></param>
    /// <returns>ApiModFilesInfo if mod exists or null</returns>
    public Task<ApiModFilesInfo?> GetModFilesInfoAsync(GbModId modId, CancellationToken cancellationToken = default);


    /// <summary>
    /// Gets the mod file info from the GameBanana API.
    /// </summary>
    /// <param name="modId">The Game banana's mod Id</param>
    /// <param name="modFileId">The Game banana's mod files Id</param>
    /// <param name="cancellationToken"></param>
    /// <returns>ApiModFileInfo if file exists or null</returns>
    [Obsolete("Use GetModFilesInfoAsync instead")]
    public Task<ApiModFileInfo?> GetModFileInfoAsync(GbModId modId, GbModFileId modFileId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if the mod file exists on GameBanana.
    /// </summary>
    public Task<bool> ModFileExists(GbModFileId modFileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Download mod file from GameBanana.
    /// </summary>
    /// <param name="modFileId">The Game banana's mod files Id</param>
    /// <param name="destinationFile">File  stream to write the contents to</param>
    /// <param name="progress">Reports to as a percentage from 0 to 100</param>
    /// <param name="cancellationToken">Cancels the download but does not delete the destinationFile</param>
    /// <exception cref="InvalidOperationException">When mod is not found</exception>
    /// <exception cref="HttpRequestException"></exception>
    public Task DownloadModAsync(GbModFileId modFileId, FileStream destinationFile, IProgress<int>? progress,
        CancellationToken cancellationToken = default);
}