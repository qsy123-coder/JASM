using System.Globalization;
using System.Text.RegularExpressions;

namespace GIMI_ModManager.Core.Services.OilGloss;

/// <summary>
/// 在**不启动游戏**的前提下，估算 Mod 里 <c>$draw_component_*</c> 这类变量的默认值。
///
/// 为什么需要：自动分组如果只看 drawindexed 的索引数，很容易选中「默认形态下不绘制」的网格
/// （例如 <c>$draw_component_3_005 = ($swapvar_toggle_0 == 1)</c>，而 toggle 默认是 0）。
/// 那样补丁打上了，但默认造型里根本没有油光，看起来就像没生效。
///
/// 只支持 WWMI 生成器常见的简单表达式：数字、<c>$变量</c>、<c>== != &lt; &lt;= &gt; &gt;=</c>、
/// <c>&amp;&amp;</c>、<c>||</c> 和括号。解析不了的赋值直接跳过（留作未知，不猜）。
/// </summary>
internal static class OilGlossConditionEvaluator
{
    private static readonly Regex GlobalDefaultRegex =
        new(@"^\s*global\s+(?:persist\s+)?\$([^\s=]+)\s*=\s*([^\s;]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex AssignmentRegex =
        new(@"^\s*\$([^\s=]+)\s*=\s*(.+)$", RegexOptions.Compiled);

    /// <summary>
    /// 收集所有 ini 的 global 默认值，并迭代求值简单赋值，返回已知变量的数值。
    /// </summary>
    public static IReadOnlyDictionary<string, double> Evaluate(IEnumerable<string> iniTexts)
    {
        var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var assignments = new List<(string Name, string Expression)>();

        foreach (var text in iniTexts)
        {
            foreach (var rawLine in text.Split('\n'))
            {
                var line = StripInlineComment(rawLine).Trim();
                if (line.Length == 0)
                    continue;

                var global = GlobalDefaultRegex.Match(line);
                if (global.Success)
                {
                    if (TryParseNumber(global.Groups[2].Value, out var number))
                        values[global.Groups[1].Value] = number;
                    continue;
                }

                var assignment = AssignmentRegex.Match(line);
                if (!assignment.Success)
                    continue;

                var name = assignment.Groups[1].Value;
                var expression = assignment.Groups[2].Value.Trim();
                if (name.StartsWith('\\') || expression.StartsWith("ref ", StringComparison.OrdinalIgnoreCase)
                    || expression.Contains("null", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                assignments.Add((name, expression));
            }
        }

        // 迭代到不动点。注意：赋值必须能**覆盖** [Constants] 里的 global 初始值——
        // WWMI 生成器常见的写法是先 `global $draw_component_3_005 = 1`，
        // 再在 [CommandListProcessToggles] 里按 $swapvar_toggle_* 重算。
        for (var pass = 0; pass < 10; pass++)
        {
            var changed = false;
            foreach (var (name, expression) in assignments)
            {
                if (!ExpressionParser.TryEvaluate(expression, values, out var result))
                    continue;

                if (!values.TryGetValue(name, out var existing) || Math.Abs(existing - result) > double.Epsilon)
                {
                    values[name] = result;
                    changed = true;
                }
            }

            if (!changed)
                break;
        }

        return values;
    }

    /// <summary>条件变量在默认配置下是否为真；变量未知时返回 <c>null</c>。</summary>
    public static bool? IsTrue(string? variable, IReadOnlyDictionary<string, double> values)
    {
        if (string.IsNullOrWhiteSpace(variable))
            return null;

        var name = variable.Trim().TrimStart('$');
        return values.TryGetValue(name, out var value) ? Math.Abs(value) > double.Epsilon : null;
    }

    private static string StripInlineComment(string line)
    {
        var index = line.IndexOf(';');
        return index >= 0 ? line[..index] : line;
    }

    private static bool TryParseNumber(string text, out double value)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    /// <summary>极小的递归下降求值器，只覆盖 3DMigoto 生成器写出的布尔表达式。</summary>
    private static class ExpressionParser
    {
        public static bool TryEvaluate(string expression, IReadOnlyDictionary<string, double> values, out double result)
        {
            var parser = new Parser(expression, values);
            try
            {
                result = parser.ParseOr();
                return parser.AtEnd;
            }
            catch (FormatException)
            {
                result = 0;
                return false;
            }
        }

        private sealed class Parser(string text, IReadOnlyDictionary<string, double> values)
        {
            private int _position;

            public bool AtEnd
            {
                get
                {
                    SkipWhiteSpace();
                    return _position >= text.Length;
                }
            }

            public double ParseOr()
            {
                var left = ParseAnd();
                while (TryConsume("||"))
                {
                    var right = ParseAnd();
                    left = left != 0 || right != 0 ? 1 : 0;
                }

                return left;
            }

            private double ParseAnd()
            {
                var left = ParseComparison();
                while (TryConsume("&&"))
                {
                    var right = ParseComparison();
                    left = left != 0 && right != 0 ? 1 : 0;
                }

                return left;
            }

            private double ParseComparison()
            {
                var left = ParsePrimary();
                if (TryConsume("=="))
                    return Math.Abs(left - ParsePrimary()) <= double.Epsilon ? 1 : 0;
                if (TryConsume("!="))
                    return Math.Abs(left - ParsePrimary()) > double.Epsilon ? 1 : 0;
                if (TryConsume(">="))
                    return left >= ParsePrimary() ? 1 : 0;
                if (TryConsume("<="))
                    return left <= ParsePrimary() ? 1 : 0;
                if (TryConsume(">"))
                    return left > ParsePrimary() ? 1 : 0;
                if (TryConsume("<"))
                    return left < ParsePrimary() ? 1 : 0;

                return left;
            }

            private double ParsePrimary()
            {
                SkipWhiteSpace();
                if (_position >= text.Length)
                    throw new FormatException();

                if (text[_position] == '(')
                {
                    _position++;
                    var inner = ParseOr();
                    SkipWhiteSpace();
                    if (_position >= text.Length || text[_position] != ')')
                        throw new FormatException();
                    _position++;
                    return inner;
                }

                if (text[_position] == '$')
                {
                    var start = ++_position;
                    while (_position < text.Length && (char.IsLetterOrDigit(text[_position]) || text[_position] is '_' or '\\'))
                        _position++;

                    var name = text[start.._position];
                    if (!values.TryGetValue(name, out var value))
                        throw new FormatException();

                    return value;
                }

                var numberStart = _position;
                if (text[_position] is '-' or '+')
                    _position++;
                while (_position < text.Length && (char.IsDigit(text[_position]) || text[_position] == '.'))
                    _position++;

                if (_position == numberStart || !TryParseNumber(text[numberStart.._position], out var number))
                    throw new FormatException();

                return number;
            }

            private bool TryConsume(string token)
            {
                SkipWhiteSpace();
                if (!text.AsSpan(_position).StartsWith(token, StringComparison.Ordinal))
                    return false;

                _position += token.Length;
                return true;
            }

            private void SkipWhiteSpace()
            {
                while (_position < text.Length && char.IsWhiteSpace(text[_position]))
                    _position++;
            }
        }
    }
}