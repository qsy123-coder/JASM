using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using GIMI_ModManager.Core.Services.GameBanana.Models;

namespace GIMI_ModManager.Core.Services.GameBanana;

public static class GameBananaUrlHelper
{
    public static bool TryGetModIdFromUrl(Uri url, [NotNullWhen(true)] out GbModId? modId)
    {
        modId = null;

        if (url.Host != "gamebanana.com" || url.Scheme != Uri.UriSchemeHttps)
            return false;


        var segments = url.Segments;

        if (segments.Length < 2)
            return false;

        modId = new GbModId(segments.Last());

        if (modId.ModId.Contains('/'))
            return false;

        return true;
    }

    /// <summary>
    /// 从游戏板块地址里抠出板块 Id（如 <c>https://gamebanana.com/games/20357</c>）。
    ///
    /// 比 <see cref="TryGetModIdFromUrl"/> 严一档：**要求路径第二段就是 <c>games</c>**，
    /// 且末段必须是正整数。故意不复用上面那个宽松实现 —— 它「取末段即认」，拿它解析
    /// <c>…/games/20357/mods</c> 会得到 "mods"，解析任意 gamebanana.com 路径也会当合法游戏 Id。
    /// 商店要拿这个 Id 去发请求，前缀错一点就是几百条错数据，宁可判严。
    /// </summary>
    public static bool TryGetGameIdFromUrl(Uri url, [NotNullWhen(true)] out GbGameId? gameId)
    {
        gameId = null;

        if (url.Host != "gamebanana.com" || url.Scheme != Uri.UriSchemeHttps)
            return false;

        var segments = url.Segments;

        // 形如 ["/", "games/", "20357"]（允许末尾多个斜杠）。
        if (segments.Length < 3 || !segments[1].Trim('/').Equals("games", StringComparison.OrdinalIgnoreCase))
            return false;

        var lastSegment = segments[^1].Trim('/');

        if (!int.TryParse(lastSegment, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
            return false;

        gameId = new GbGameId(id);
        return true;
    }
}