using Newtonsoft.Json;

namespace GIMI_ModManager.WinUI.Models.Settings;

/// <summary>
/// 侧栏「角色」那一节的**计数与图标**缓存，用来跨启动活下来。
///
/// 为什么要落盘：角色计数没有批量端点（实测没有），只能一个角色一个搜索请求 —— 五十多个角色
/// 就算并发补也要十几秒，还得排队过限流桶。回合内本来就有内存缓存（<c>ModStoreService</c> 是单例），
/// 但进程一重启就没了，每次开应用都要重等一遍。落盘之后第二次进页面是**零请求**秒出。
///
/// 为什么存 <c>SettingScope.Game</c> 而不是 App：这份数据是**按板块**取的 —— 同一个角色名在不同
/// 板块下命中数完全不同。跟着游戏走天然不会串味，也省得再存一份 game id 去比对。
///
/// 过期策略：整份数据一个时间戳，超过 <see cref="Ttl"/> 就当没有。计数确实会变，但晚一天刷新
/// 没人察觉得到，而「每次开应用都重问五十多个请求」是天天都要付的代价。
/// </summary>
public class ModStoreSidebarCache
{
    [JsonIgnore] public const string Key = "ModStoreSidebarCache";

    /// <summary>整份缓存的保鲜期。</summary>
    [JsonIgnore] public static readonly TimeSpan Ttl = TimeSpan.FromHours(24);

    /// <summary>写这份缓存的时间（UTC，落盘时由页面填）。</summary>
    public DateTimeOffset FetchedAt { get; set; }

    /// <summary>角色名 → 摘要。不区分大小写：GameBanana 的角色名大小写不一定和本地表一致。</summary>
    public Dictionary<string, CachedSummary> Characters { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>这份缓存还新鲜吗（过了 <see cref="Ttl"/> 就当没有）。</summary>
    public bool IsFresh(DateTimeOffset now) => now - FetchedAt < Ttl;

    public sealed class CachedSummary
    {
        /// <summary>命中数。<c>null</c> = 上次也没问到（界面不显示数字）。</summary>
        public int? Count { get; set; }

        /// <summary>角色图标地址。不少子分类没设过图，<c>null</c> 是正常的。</summary>
        public string? IconUrl { get; set; }
    }
}