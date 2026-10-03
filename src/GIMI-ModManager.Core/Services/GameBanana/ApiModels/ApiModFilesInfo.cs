using System.Text.Json.Serialization;

namespace GIMI_ModManager.Core.Services.GameBanana.ApiModels;

/// <summary>
/// <c>apiv11/Mod/{id}/DownloadPage</c>（文件清单）。
///
/// ⚠️ 文件列表落在**两个**字段里，取清单必须两个都读：
/// <list type="bullet">
///   <item><c>_aFiles</c>：活跃文件 —— 正常 mod 有（575376 给 4 条）；</item>
///   <item><c>_aArchivedFiles</c>：归档文件 —— **不是隐藏 mod 专有**：普通 mod 也会带一条旧版本
///         （575376 / 537550 各 1 条），而被隐藏的 709792 <c>_aFiles</c> 整个键都不存在、
///         文件全在这个字段里。</item>
/// </list>
/// 只读 <see cref="Files"/> 会把 709792 那种当成「这个 mod 没有文件」，只读归档字段则会让
/// 普通 mod 的旧版本挤掉当前版本（合并见 <c>ModStoreDetail</c>：活跃在前、归档在后）。
/// </summary>
public class ApiModFilesInfo
{
    [JsonPropertyName("_bIsTrashed")] public bool IsTrashed { get; init; }
    [JsonPropertyName("_bIsWithheld")] public bool IsWithheld { get; init; }

    /// <summary>活跃文件。⚠️ DTO 上声明为非空，但 JSON 里**真的可能没有这个键**（709792）——
    /// 那时反序列化结果是 null 而不是空集合，别信声明（<c>ModStoreDetail</c> 判了两次）。</summary>
    [JsonPropertyName("_aFiles")] public ICollection<ApiModFileInfo> Files { get; init; } = [];

    /// <summary>归档文件。键缺失时是空集合（不是 null），可放心遍历。</summary>
    [JsonPropertyName("_aArchivedFiles")]
    public ICollection<ApiModFileInfo> ArchivedFiles { get; init; } = [];
}