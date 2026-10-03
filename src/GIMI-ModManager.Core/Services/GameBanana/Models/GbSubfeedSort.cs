namespace GIMI_ModManager.Core.Services.GameBanana.Models;

/// <summary>
/// GameBanana 列表接口支持的排序方式。
///
/// ⚠️ API 参数名是 <c>_sSort</c>，**不是</c> <c>sort</c> —— 用 <c>sort=</c> 传值不报错，
/// 只会被静默忽略（返回结果与默认排序逐条一致），这一点实测踩过。改这里之前先看
/// <c>docs/mod-store-prd.md</c> 的「已验证的 GameBanana API 事实」。
///
/// 取值同样是实测结果：<c>newest</c> 会返回 HTTP 400（说明该参数确实被解析），
/// 而「按点赞数 / 下载量」排序对应的取值尚未枚举出来 —— 需要时先补实测再加成员，
/// 别凭直觉写一个塞进去（那会静默退化成默认排序）。
/// </summary>
public enum GbSubfeedSort
{
    /// <summary>默认：GameBanana 自己的热度口径。</summary>
    Default,

    /// <summary>最新发布。</summary>
    New,

    /// <summary>最近更新。</summary>
    Updated
}

public static class GbSubfeedSortExtensions
{
    /// <summary>映射为 <c>_sSort</c> 的查询取值。</summary>
    public static string ToApiValue(this GbSubfeedSort sort) => sort switch
    {
        GbSubfeedSort.New => "new",
        GbSubfeedSort.Updated => "updated",
        _ => "default"
    };
}