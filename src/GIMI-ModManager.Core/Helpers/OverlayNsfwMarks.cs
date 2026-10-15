using Newtonsoft.Json;

namespace GIMI_ModManager.Core.Helpers;

/// <summary>
/// 浮窗里的「NSFW 标记」：一件一件过 Mod 时把看到的那件打上标，攒成一份名单。
///
/// <para>
/// 这是**审阅用的临时记录**，不是 Mod 的元数据：标记只写在这里，不碰 Mod 的文件夹名、
/// 也不动它自己的设置文件 —— 打错了一下右键就能反悔，而且反悔不会改变游戏里的表现。
/// </para>
///
/// <para>
/// 名单落在 <c>LocalAppSettings.json</c>（App 作用域）里，是纯文本且带着 Mod 名与角色，
/// 目的就是让「哪些要下架」这份结论能被人直接读走。
/// </para>
///
/// <para>
/// 比对一律走 <c>SkinMod.Id</c>（存在每个 Mod 自己的设置文件里，跨重启稳定）；
/// 名字与角色只是**给人看的副本**，改名/换语言都不会让它变成判据。
/// </para>
/// </summary>
public sealed class OverlayNsfwMarks
{
    /// <summary>在设置文件里的键名，与 <c>OverlaySettings.Key</c> 同级。</summary>
    [JsonIgnore] public const string Key = "OverlayNsfwMarks";

    /// <summary>
    /// 标记过的 Mod。
    ///
    /// **刻意不清理**：Mod 被删掉或改名之后，它那条记录仍然留着。这份名单是拿去下架的，
    /// 名字留在名单里比「对象没了就把记录也删了」有用得多；而读它的时候只按 Id 比对，
    /// 多出来的记录也不影响界面。
    /// </summary>
    public List<OverlayNsfwMarkEntry> Entries { get; set; } = new();

    /// <summary>标记条数。设置文件被手改坏（<c>Entries</c> 为 <c>null</c>）时按 0 算，不抛。</summary>
    [JsonIgnore] public int Count => Entries?.Count ?? 0;

    /// <summary>这个 Mod 标过没有。</summary>
    public bool IsMarked(Guid modId) => Entries?.Any(entry => entry.Id == modId) == true;

    /// <summary>
    /// 切换标记，返回**切换之后**的状态（<c>true</c> = 现在是标上的）。
    /// </summary>
    /// <param name="modId">Mod 的稳定标识（<c>SkinMod.Id</c>）。</param>
    /// <param name="character">所属角色的 <c>InternalName</c>，只写进名单给人看。</param>
    /// <param name="name">标记时刻的显示名，只写进名单给人看。</param>
    public bool Toggle(Guid modId, string character, string name)
    {
        // 手改坏、或老版本没有这个字段时反序列化出来是 null —— 在这里补回来，
        // 别让一个坏文件把「标记」这个功能整个弄哑。
        Entries ??= new List<OverlayNsfwMarkEntry>();

        var existing = Entries.FirstOrDefault(entry => entry.Id == modId);

        if (existing is not null)
        {
            Entries.Remove(existing);
            return false;
        }

        Entries.Add(new OverlayNsfwMarkEntry
        {
            Id = modId,
            Character = character,
            Name = name,
            MarkedAt = DateTimeOffset.Now
        });

        return true;
    }
}

/// <summary>
/// 名单里的一条。
///
/// 角色存 <c>InternalName</c>（与 <c>OverlaySettings.LastSelectedCharacter</c> 同口径）：
/// 显示名会随语言变、也可能被改，拿它当记录会在换语言后对不上人。
/// </summary>
public sealed class OverlayNsfwMarkEntry
{
    public Guid Id { get; set; }

    public string Character { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>打标的时间。名单里有序可循，也方便日后回看是哪一轮审的。</summary>
    public DateTimeOffset MarkedAt { get; set; }
}