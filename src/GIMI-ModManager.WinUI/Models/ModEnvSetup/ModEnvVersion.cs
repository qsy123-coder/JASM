namespace GIMI_ModManager.WinUI.Models.ModEnvSetup;

/// <summary>
/// Ordering helpers for dotted numeric version strings (e.g. "1.1.7").
/// </summary>
/// <remarks>
/// The ModEnv pipeline historically decided "is there an update?" with a plain string inequality
/// (installed != manifest). That was adequate while only one version was ever offered, but once the user
/// can pick an older version the <em>direction</em> matters: installing 0.9.2 over 1.1.7 is a rollback,
/// not an update, and a string compare cannot tell the two apart — lexicographically "1.1.7" sorts
/// before "0.9.2", which would label a rollback as an upgrade.
/// </remarks>
public static class ModEnvVersion
{
    /// <summary>
    /// Compares two versions segment by segment; negative when <paramref name="left"/> is the older one.
    /// Falls back to an ordinal compare when a segment is not numeric, so malformed CDN data can never throw.
    /// </summary>
    public static int Compare(string left, string right)
    {
        var leftParts = left.Split('.');
        var rightParts = right.Split('.');
        var length = Math.Max(leftParts.Length, rightParts.Length);

        for (var index = 0; index < length; index++)
        {
            var leftValue = ParseSegment(leftParts, index);
            var rightValue = ParseSegment(rightParts, index);

            if (leftValue is null || rightValue is null)
                return string.CompareOrdinal(left, right);

            if (leftValue.Value != rightValue.Value)
                return leftValue.Value.CompareTo(rightValue.Value);
        }

        return 0;
    }

    /// <summary>Sort-order helper for newest-first lists.</summary>
    public static int CompareDescending(string left, string right) => Compare(right, left);

    /// <summary>True when <paramref name="candidate"/> predates <paramref name="reference"/>.</summary>
    public static bool IsOlder(string candidate, string reference) => Compare(candidate, reference) < 0;

    private static int? ParseSegment(string[] parts, int index)
    {
        if (index < parts.Length && int.TryParse(parts[index], out var value))
            return value;

        return null;
    }
}
