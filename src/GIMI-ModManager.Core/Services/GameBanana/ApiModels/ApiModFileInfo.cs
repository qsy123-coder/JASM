using System.Text.Json.Serialization;

namespace GIMI_ModManager.Core.Services.GameBanana.ApiModels;

public class ApiModFileInfo
{
    [JsonPropertyName("_idRow")] public int FileId { get; init; } = -1;
    [JsonPropertyName("_sFile")] public string FileName { get; init; } = null!;
    [JsonPropertyName("_sDownloadUrl")] public string DownloadUrl { get; init; } = null!;

    /// <summary>
    /// Unix 秒。⚠️ 用 <c>long</c> 而不是 <c>int</c>：GameBanana 自己的文档写的是 int，
    /// 但 2038 年之后（或万一给了毫秒）会把**整页反序列化**打挂 —— 一个时间戳不值得这个代价。
    /// </summary>
    [JsonPropertyName("_tsDateAdded")] public long DateAdded { get; init; }

    [JsonPropertyName("_sDescription")] public string Description { get; init; } = null!;

    /// <summary>
    /// 字节数。同样用 <c>long</c>：实测单个文件已经到 917 MB，GB 上有 &gt;2 GB 的包，
    /// 而 <c>int</c> 上限是 2.1 GB —— 溢出会让**整页反序列化失败**（不是那一条为 0）。
    /// </summary>
    [JsonPropertyName("_nFilesize")] public long FileSize { get; init; } = -1;

    [JsonPropertyName("_sAnalysisResultCode")]
    public string AnalysisResultCode { get; init; } = null!;

    [JsonPropertyName("_sMd5Checksum")] public string Md5Checksum { get; init; } = null!;

    [JsonPropertyName("_nDownloadCount")] public int DownloadCount { get; init; } = -1;

    /// <summary>
    /// 文件级版本号。
    /// ⚠️ 实测**经常整个键都不存在**（同一个 mod 的 4 个文件里可能一个都没有，见 mod 575376），
    /// 所以它可空，界面要退回显示 mod 级版本号。
    /// </summary>
    [JsonPropertyName("_sVersion")] public string? Version { get; init; }

    /// <summary>
    /// 归档标记。实测与**所在数组**一致：<c>_aFiles</c> 里恒为 false、
    /// <c>_aArchivedFiles</c> 里恒为 true —— 但它仍然值得单独映射，别只靠数组判断。
    /// </summary>
    [JsonPropertyName("_bIsArchived")] public bool IsArchived { get; init; }
}