using System.Text;
using GIMI_ModManager.Core.GamesService.Interfaces;

namespace GIMI_ModManager.Core.Helpers;

/// <summary>一个角色与线索的匹配结果。<see cref="Score"/> 越大越可信。</summary>
public sealed record CharacterNameMatch(ICharacter Character, int Score)
{
    /// <summary>内部名（已是小写），调用方用它查 <c>ICharacterModList</c>。</summary>
    public string InternalName => Character.InternalName.Id;

    public string DisplayName => Character.DisplayName;
}

/// <summary>
/// 「这个包是谁的」—— 从若干条线索（原文件名、解压后的包内顶层目录名）里认角色。
///
/// <para>
/// **为什么不复用 <c>IGameService.QueryCharacters</c>**：它走 FuzzySharp 的整串比对，长文件名会被长度稀释。
/// 实测推演：<c>爱弥斯-誓约（0）by 晨星</c> 与 <c>Aemeath</c> 的 <c>Fuzz.Ratio</c> 约 58 分，而它的采纳阈值是 100
/// —— 一个都过不了。所以这里先按分隔符切词，再拿**词**与角色别名比。
/// </para>
///
/// <para>
/// **为什么不做模糊匹配**（刻意为之）：真实语料（桌面那 20+ 个 Mod SFX 包名）已被下面两条规则全覆盖；
/// 而短中文名（两三个字）的模糊匹配假阳性风险大于收益 —— 认错角色会让 Mod 落到别的角色文件夹里。
/// 数据里要加新说法，正确做法是往 <c>characters.json</c> 的 <c>Keys</c> 里加一条（那本来就是它的用途），
/// 而不是在这里放宽匹配。
/// </para>
///
/// <para>
/// 别名池 = <c>Keys</c> ∪ {<c>InternalName</c>, <c>ModFilesName</c>, <c>DisplayName</c>}。
/// 注意这些都是**运行时**的值：<c>Languages/&lt;lang&gt;/characters.json</c> 会在加载时把
/// 中文名并进 <c>Keys</c>（实测 WuWa 的 Aemeath：<c>Keys = ["aemeath", "爱弥斯"]</c>、<c>DisplayName = "爱弥斯"</c>），
/// 所以中文包名靠的就是它们。
/// </para>
///
/// <para>
/// 两条规则（都忽略大小写）：
/// <list type="number">
/// <item><b>整词命中 = <see cref="ExactAliasScore"/></b>：别名与某个词**完全相等**。任何长度都认 ——
/// 数据里真有单字别名（WuWa 的「椿」= Camellya）和两字母别名（Genshin 的「hu」），加长度守卫会把它们误伤。</item>
/// <item><b>整条线索命中 = <see cref="ExactAliasScore"/></b>：把**整条线索**和别名都去掉分隔符后相等
/// （<c>Hu Tao</c> ≡ <c>HuTao</c>、<c>泱泱・玄翎</c> ≡ <c>泱泱玄翎</c>）。同一个名字在数据里是
/// <c>HuTao</c>、在包内目录里写成 <c>Hu Tao</c> 是常事 —— 光靠切词会把这种名字切散（<c>Hu</c> + <c>Tao</c>
/// 两个词都不是完整名字）。因为要求整条相等，不会比规则 1 更松。</item>
/// <item><b>词里含别名 = <see cref="AliasInsideTokenScore"/></b>：词比别名长（<c>千咲大凤兔女郎</c> 含 <c>千咲</c>）。
/// 这条有守卫，见 <see cref="IsStrongEnoughForContainment"/>。**只单向**（词含别名，不反过来）：
/// 别名里含某个词 = 只命中包名的一个碎片，证据太弱；真需要的话该由数据里的 <c>Keys</c> 来表达。</item>
/// </list>
/// </para>
///
/// <para>
/// 最后一条是**不猜**：即使有最高分，若与第二名的差距不够（<see cref="IsConfident"/>），也交给用户选，
/// 别自动落地。
/// </para>
/// </summary>
public static class CharacterNameMatcher
{
    /// <summary>别名与词完全相等。</summary>
    public const int ExactAliasScore = 100;

    /// <summary>词里包含了别名（词更长）。</summary>
    public const int AliasInsideTokenScore = 85;

    /// <summary>低于这个分就不算「认出来了」。</summary>
    public const int AutoAdoptMinScore = AliasInsideTokenScore;

    /// <summary>最高分与第二名的分差低于这个值就不自动落地（可能是两个同名 / 相近的角色）。</summary>
    public const int AutoAdoptMinMargin = 15;

    /// <summary>参与「包含匹配」的词的最短长度。单字词（<c>爱 琳 莫</c> 这种被空格切开的描述词）噪声太大。</summary>
    private const int MinTokenLengthForContainment = 2;

    /// <summary>纯 ASCII 别名参与「包含匹配」的最短长度（<c>hu</c> 会命中 <c>HuTao</c> 之外的一堆东西）。</summary>
    private const int MinAsciiAliasLengthForContainment = 3;

    /// <summary>非 ASCII（中文）别名参与「包含匹配」的最短长度。</summary>
    private const int MinUnicodeAliasLengthForContainment = 2;

    /// <summary>
    /// 给每个角色打分，按分数从高到低排（同分按内部名排，保证结果稳定）。没有命中的角色不会出现在结果里。
    /// </summary>
    /// <param name="characters">候选角色（一般直接给 <c>IGameService.GetAllModdableObjectsAsCategory&lt;ICharacter&gt;()</c>）。</param>
    /// <param name="clues">线索字符串：原文件名、包内顶层目录名等；切词与大小写由这里负责，调用方不用预处理。</param>
    /// <param name="excludedInternalNames">
    /// 要跳过的内部名。**务必**传入 <c>Others</c> / <c>Gliders</c> / <c>Weapons</c>
    /// （<c>IGameService.OtherCharacterInternalName</c> 等）：这三个伪角色的 <c>Keys</c> 是
    /// <c>sword</c>/<c>bow</c>/<c>weapon</c> 这类通用词（中文名更是「武器」这种两字词），
    /// 不排除的话「Mornye sword skin」会被认成「武器」。
    /// </param>
    public static IReadOnlyList<CharacterNameMatch> Rank(IEnumerable<ICharacter>? characters,
        IEnumerable<string?>? clues, IEnumerable<string>? excludedInternalNames = null)
    {
        var result = new List<CharacterNameMatch>();
        if (characters is null || clues is null) return result;

        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalizedClues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var clue in clues)
        {
            if (string.IsNullOrWhiteSpace(clue)) continue;

            foreach (var token in Tokenize(clue))
                tokens.Add(token);

            normalizedClues.Add(Normalize(clue));
        }

        if (tokens.Count == 0) return result;

        foreach (var character in characters)
        {
            if (character is null) continue;
            if (IsExcluded(character, excludedInternalNames)) continue;

            var score = Score(character, tokens, normalizedClues);
            if (score > 0)
                result.Add(new CharacterNameMatch(character, score));
        }

        return result
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.InternalName, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// 这个结果能不能直接落地（不必问用户）。要求：最高分够高，且领先第二名足够多。
    /// </summary>
    public static bool IsConfident(IReadOnlyList<CharacterNameMatch>? ranked)
    {
        if (ranked is null || ranked.Count == 0) return false;
        if (ranked[0].Score < AutoAdoptMinScore) return false;
        if (ranked.Count == 1) return true;

        return ranked[0].Score - ranked[1].Score >= AutoAdoptMinMargin;
    }

    private static int Score(ICharacter character, IReadOnlySet<string> tokens,
        IReadOnlySet<string> normalizedClues)
    {
        foreach (var alias in EnumerateAliases(character))
        {
            // 整词相等。**不加长度守卫** —— 数据里存在单字「椿」与两字母「hu」这样的别名
            if (tokens.Contains(alias)) return ExactAliasScore;

            // 整条线索（去掉分隔符后）就是别名
            if (normalizedClues.Contains(Normalize(alias))) return ExactAliasScore;

            // 词里含别名
            if (tokens.Any(token => IsStrongEnoughForContainment(alias, token) &&
                                    token.Contains(alias, StringComparison.OrdinalIgnoreCase)))
                return AliasInsideTokenScore;
        }

        return 0;
    }

    /// <summary>
    /// 规则 B 的守卫：词不能太短（单字词噪声大，见 <see cref="MinTokenLengthForContainment"/>），
    /// 别名也不能太短（见两个 MinLength 常量）。
    /// </summary>
    private static bool IsStrongEnoughForContainment(string alias, string token)
    {
        if (token.Length < MinTokenLengthForContainment) return false;

        var minLength = IsAsciiOnly(alias)
            ? MinAsciiAliasLengthForContainment
            : MinUnicodeAliasLengthForContainment;

        return alias.Length >= minLength;
    }

    private static bool IsAsciiOnly(string value) => value.All(c => c <= 0x7F);

    private static IEnumerable<string> EnumerateAliases(ICharacter character)
    {
        yield return character.InternalName.Id;

        if (!string.IsNullOrWhiteSpace(character.ModFilesName))
            yield return character.ModFilesName;

        if (!string.IsNullOrWhiteSpace(character.DisplayName))
            yield return character.DisplayName;

        if (character.Keys is null) yield break;

        foreach (var key in character.Keys)
        {
            if (!string.IsNullOrWhiteSpace(key))
                yield return key;
        }
    }

    private static bool IsExcluded(ICharacter character, IEnumerable<string>? excludedInternalNames)
    {
        if (excludedInternalNames is null) return false;

        // InternalNameEquals 是仓库自己的比较（忽略大小写），别手写比较
        return excludedInternalNames.Any(name => character.InternalNameEquals(name));
    }

    /// <summary>
    /// 去掉所有分隔符并转小写（<c>Hu Tao</c> → <c>hutao</c>），用于「整条线索就是别名」那条规则。
    /// </summary>
    private static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);

        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
                builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }

    /// <summary>
    /// 切词：只有字母 / 数字算词内字符，其余（空格、<c>-</c>、<c>_</c>、<c>.</c>、<c>（）</c>、<c>【】</c>、<c>+</c>、<c>~</c>、<c>・</c>…）
    /// 一律当分隔符。
    ///
    /// <para>
    /// 用 <see cref="char.IsLetterOrDigit(char)"/> 而不是手写分隔符表，是为了不漏掉全角标点、emoji、书名号这些
    /// 包名里可能出现的东西 —— 漏一个就会切出「誓约（0」这种带标点的词，后面一条都匹配不上。中文汉字本身是 letter，
    /// 所以不会被切开，正好。
    /// </para>
    /// </summary>
    private static IEnumerable<string> Tokenize(string clue)
    {
        var start = -1;

        for (var i = 0; i < clue.Length; i++)
        {
            if (char.IsLetterOrDigit(clue[i]))
            {
                if (start < 0) start = i;
                continue;
            }

            if (start >= 0)
            {
                yield return clue.Substring(start, i - start);
                start = -1;
            }
        }

        if (start >= 0)
            yield return clue.Substring(start);
    }
}