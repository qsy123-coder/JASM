using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using GIMI_ModManager.Core.Helpers;
using Serilog;

namespace GIMI_ModManager.Core.Entities.Mods.Helpers;

/// <summary>
/// 扫描 mod 文件夹中所有 .ini 文件，提取按键绑定。
/// 支持两种 3Dmigoto 格式：
///   1. [KeyXxx] 段落：下一行是 modifier+key 格式（如 no_ctrl no_shift VK_RIGHT）
///   2. 普通段落中的 key = value 格式
/// </summary>
public static class ModIniKeyBindingParser
{
    private static readonly ILogger _logger = Log.ForContext(typeof(ModIniKeyBindingParser));

    public static List<ModIniKeyBindingGroup> ParseAllKeyBindings(string modFolderPath)
    {
        var result = new List<ModIniKeyBindingGroup>();

        if (!Directory.Exists(modFolderPath))
            return result;

        var iniFiles = Directory.GetFiles(modFolderPath, "*.ini", SearchOption.AllDirectories);
        _logger.Information("[KeyBindingParser] 扫描 {Path}, 找到 {Count} 个 ini 文件",
            modFolderPath, iniFiles.Length);

        foreach (var iniFilePath in iniFiles)
        {
            _logger.Information("[KeyBindingParser] 解析: {File}", iniFilePath);
            var bindings = ParseKeyBindingsFromFile(iniFilePath);
            _logger.Information("[KeyBindingParser]   → 找到 {Count} 条绑定", bindings.Count);

            foreach (var b in bindings)
            {
                _logger.Information("[KeyBindingParser]     [{Section}] KeyValue='{Key}' IsArrow={Arrow} Action='{Action}' Desc='{Desc}'",
                    b.SectionName, b.KeyValue, b.IsArrowKey, b.ActionLabel, b.Description);
            }

            if (bindings.Count == 0)
                continue;

            var relativePath = Path.GetRelativePath(modFolderPath, iniFilePath);

            result.Add(new ModIniKeyBindingGroup
            {
                IniFileRelativePath = relativePath,
                IniFileFullPath = Path.Combine(modFolderPath, relativePath),
                Bindings = bindings
            });
        }

        return result;
    }

    private static List<ModIniKeyBindingEntry> ParseKeyBindingsFromFile(string iniFilePath)
    {
        var bindings = new List<ModIniKeyBindingEntry>();
        string? currentSection = null;
        string? lastComment = null;
        var lines = File.ReadAllLines(iniFilePath);

        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();

            // 空行：清注释
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                lastComment = null;
                continue;
            }

            // 注释行：缓存
            if (trimmed.StartsWith(';'))
            {
                lastComment = CleanComment(trimmed);
                continue;
            }

            // 段落头
            if (IniConfigHelpers.IsSection(trimmed))
            {
                currentSection = trimmed;
                continue;
            }

            // 在段落内检测按键绑定
            if (currentSection is not null)
            {
                // 跳过 condition / type / $swapvar 等非按键行
                if (IniConfigHelpers.IsIniKey(trimmed, "condition")
                    || IniConfigHelpers.IsIniKey(trimmed, "type")
                    || IniConfigHelpers.IsIniKey(trimmed, "$swapvar")
                    || IniConfigHelpers.IsIniKey(trimmed, "back")
                    || IniConfigHelpers.IsIniKey(trimmed, "run"))
                    continue;

                // 格式1: key = value（value 本身可能含 modifier+key，如 "NO_CTRL NO_ALT VK_UP"）
                if (IniConfigHelpers.IsIniKey(trimmed, "key"))
                {
                    var value = IniConfigHelpers.GetIniValue(trimmed);
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        // 尝试用 modifier+key 格式解析 value
                        var parsed = ParseKeyActionLine(value);
                        bindings.Add(new ModIniKeyBindingEntry
                        {
                            SectionName = currentSection,
                            KeyValue = parsed?.KeyDisplay ?? FriendlyKeyName(value),
                            RawLine = trimmed,
                            ActionLabel = IsKeyActionSection(currentSection)
                                ? SectionToAction(currentSection) : currentSection,
                            Description = lastComment ?? "",
                            IsArrowKey = parsed?.IsArrow ?? IsArrowKeyValue(value)
                        });
                        lastComment = null;
                    }
                    continue;
                }

                // 格式2: 对于 [Key*] 段落，也检测 modifier+key 格式行
                //         如 "no_ctrl no_shift VK_RIGHT"、"ctrl shift H"
                if (IsKeyActionSection(currentSection) && IsModifierKeyLine(trimmed))
                {
                    var parsed = ParseKeyActionLine(trimmed);
                    if (parsed is not null)
                    {
                        bindings.Add(new ModIniKeyBindingEntry
                        {
                            SectionName = currentSection,
                            KeyValue = parsed.Value.KeyDisplay,
                            RawLine = trimmed,
                            ActionLabel = SectionToAction(currentSection),
                            Description = lastComment ?? "",
                            IsArrowKey = parsed.Value.IsArrow
                        });
                        lastComment = null;
                    }
                }
            }
        }

        return bindings;
    }

    // ── [KeyXxx] 段落解析 ──────────────────────────────

    /// <summary>
    /// 检测一行是否是 modifier+key 格式的按键定义行。
    /// 如 "no_ctrl no_shift VK_RIGHT"、"ctrl shift H"、"no_ctrl no_shift VK_LBUTTON"
    /// 排除条件行如 "condition = $object_detected"
    /// </summary>
    private static bool IsModifierKeyLine(string line)
    {
        if (line.Contains('=')) return false; // 不是 key=value 格式
        if (line.StartsWith(';')) return false;

        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;

        // 至少包含一个 VK_ 键名，或 modifier 词 + 单字母/数字键
        bool hasVk = false;
        bool hasModifier = false;
        bool hasKeyChar = false;

        foreach (var part in parts)
        {
            var upper = part.ToUpperInvariant();
            if (upper.StartsWith("VK_")) { hasVk = true; continue; }
            if (upper is "CTRL" or "SHIFT" or "ALT"
                or "NO_CTRL" or "NO_SHIFT" or "NO_ALT") { hasModifier = true; continue; }
            // 可能是普通键名（单字母或数字）
            if (part.Length <= 3) hasKeyChar = true;
        }

        return hasVk || (hasModifier && hasKeyChar);
    }

    /// <summary>是否是按键动作段落：[KeyHold], [KeyHoldShape], [KeyClickedSlot], [KeyToggle] 等</summary>
    private static bool IsKeyActionSection(string section)
    {
        // 去掉方括号后检查是否以 Key 开头
        var name = section.Trim('[', ']');
        return name.StartsWith("Key", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 解析 modifier+key 行，如：
    ///   "no_ctrl no_shift VK_RIGHT" → (KeyDisplay="→", IsArrow=true)
    ///   "ctrl shift H"              → (KeyDisplay="Ctrl+Shift+H", IsArrow=false)
    ///   "no_ctrl no_shift VK_LBUTTON" → (KeyDisplay="🖱 左键", IsArrow=false)
    /// </summary>
    private static (string KeyDisplay, bool IsArrow)? ParseKeyActionLine(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;

        var activeModifiers = new List<string>();
        string? keyName = null;
        var skippedMods = new List<string>();

        foreach (var part in parts)
        {
            var lower = part.ToLowerInvariant();

            if (lower is "no_ctrl" or "no_shift" or "no_alt")
            {
                skippedMods.Add(part);
                continue;
            }

            if (lower is "ctrl" or "shift" or "alt")
                activeModifiers.Add(MapModifier(lower));
            else
                keyName = part;
        }

        if (keyName is null)
        {
            _logger.Warning("[KeyBindingParser]     无法识别的按键行: '{Line}' (parts={Parts})",
                line, string.Join(", ", parts));
            return null;
        }

        var (friendlyKey, isArrow) = MapKeyName(keyName);
        var modPrefix = activeModifiers.Count > 0
            ? string.Join("+", activeModifiers) + "+"
            : "";

        var result = modPrefix + friendlyKey;
        _logger.Information("[KeyBindingParser]     RAW='{Line}' → parts={Parts} | mods={Mods} | key='{Key}' → friendly='{Result}' isArrow={Arrow}",
            line, string.Join(",", parts), string.Join(",", skippedMods), keyName, result, isArrow);

        return (result, isArrow);
    }

    private static string MapModifier(string mod) => mod.ToLowerInvariant() switch
    {
        "ctrl" => "Ctrl",
        "shift" => "Shift",
        "alt" => "Alt",
        _ => mod
    };

    /// <summary>将 VK_xxx 按键名映射为小白友好的显示</summary>
    private static (string display, bool isArrow) MapKeyName(string keyName)
    {
        var k = keyName.Trim().ToUpperInvariant();
        return k switch
        {
            // 方向键 → 箭头图标字符
            "VK_RIGHT" or "RIGHT" => ("→", true),
            "VK_LEFT" or "LEFT" => ("←", true),
            "VK_UP" or "UP" => ("↑", true),
            "VK_DOWN" or "DOWN" => ("↓", true),

            // 鼠标按键
            "VK_LBUTTON" or "LBUTTON" => ("鼠标左键", false),
            "VK_RBUTTON" or "RBUTTON" => ("鼠标右键", false),
            "VK_MBUTTON" or "MBUTTON" => ("鼠标中键", false),

            // 功能键
            "VK_F1" or "F1" => ("F1", false),
            "VK_F2" or "F2" => ("F2", false),
            "VK_F3" or "F3" => ("F3", false),
            "VK_F4" or "F4" => ("F4", false),
            "VK_F5" or "F5" => ("F5", false),
            "VK_F6" or "F6" => ("F6", false),
            "VK_F7" or "F7" => ("F7", false),
            "VK_F8" or "F8" => ("F8", false),
            "VK_F9" or "F9" => ("F9", false),
            "VK_F10" or "F10" => ("F10", false),
            "VK_F11" or "F11" => ("F11", false),
            "VK_F12" or "F12" => ("F12", false),

            // 特殊键
            "VK_SPACE" or "SPACE" => ("空格", false),
            "VK_RETURN" or "RETURN" or "ENTER" => ("回车", false),
            "VK_TAB" or "TAB" => ("Tab", false),
            "VK_ESCAPE" or "ESCAPE" or "ESC" => ("Esc", false),
            "VK_BACK" or "BACKSPACE" => ("退格", false),
            "VK_DELETE" or "DELETE" => ("Delete", false),
            "VK_HOME" => ("Home", false),
            "VK_END" => ("End", false),
            "VK_PRIOR" or "PAGEUP" => ("PgUp", false),
            "VK_NEXT" or "PAGEDOWN" => ("PgDn", false),

            // VK_数字
            "VK_0" or "0" => ("0", false),
            "VK_1" or "1" => ("1", false),
            "VK_2" or "2" => ("2", false),
            "VK_3" or "3" => ("3", false),
            "VK_4" or "4" => ("4", false),
            "VK_5" or "5" => ("5", false),
            "VK_6" or "6" => ("6", false),
            "VK_7" or "7" => ("7", false),
            "VK_8" or "8" => ("8", false),
            "VK_9" or "9" => ("9", false),

            // 小键盘
            "VK_NUMPAD0" or "NUMPAD0" => ("小键盘0", false),
            "VK_NUMPAD1" or "NUMPAD1" => ("小键盘1", false),
            "VK_NUMPAD2" or "NUMPAD2" => ("小键盘2", false),
            "VK_NUMPAD3" or "NUMPAD3" => ("小键盘3", false),
            "VK_NUMPAD4" or "NUMPAD4" => ("小键盘4", false),
            "VK_NUMPAD5" or "NUMPAD5" => ("小键盘5", false),
            "VK_NUMPAD6" or "NUMPAD6" => ("小键盘6", false),
            "VK_NUMPAD7" or "NUMPAD7" => ("小键盘7", false),
            "VK_NUMPAD8" or "NUMPAD8" => ("小键盘8", false),
            "VK_NUMPAD9" or "NUMPAD9" => ("小键盘9", false),

            // VK_字母
            "VK_A" or "A" => ("A", false),
            "VK_B" or "B" => ("B", false),
            "VK_C" or "C" => ("C", false),
            "VK_D" or "D" => ("D", false),
            "VK_E" or "E" => ("E", false),
            "VK_F" or "F" => ("F", false),
            "VK_G" or "G" => ("G", false),
            "VK_H" or "H" => ("H", false),
            "VK_I" or "I" => ("I", false),
            "VK_J" or "J" => ("J", false),
            "VK_K" or "K" => ("K", false),
            "VK_L" or "L" => ("L", false),
            "VK_M" or "M" => ("M", false),
            "VK_N" or "N" => ("N", false),
            "VK_O" or "O" => ("O", false),
            "VK_P" or "P" => ("P", false),
            "VK_Q" or "Q" => ("Q", false),
            "VK_R" or "R" => ("R", false),
            "VK_S" or "S" => ("S", false),
            "VK_T" or "T" => ("T", false),
            "VK_U" or "U" => ("U", false),
            "VK_V" or "V" => ("V", false),
            "VK_W" or "W" => ("W", false),
            "VK_X" or "X" => ("X", false),
            "VK_Y" or "Y" => ("Y", false),
            "VK_Z" or "Z" => ("Z", false),

            // 其他常见 VK_
            "VK_OEM_PERIOD" or "OEM_PERIOD" => (".", false),
            "VK_OEM_COMMA" or "OEM_COMMA" => (",", false),
            "VK_OEM_MINUS" or "OEM_MINUS" => ("-", false),
            "VK_OEM_PLUS" or "OEM_PLUS" => ("=", false),
            "VK_OEM_1" => (";", false),
            "VK_OEM_2" => ("/", false),
            "VK_OEM_3" => ("`", false),
            "VK_OEM_4" => ("[", false),
            "VK_OEM_5" => ("\\", false),
            "VK_OEM_6" => ("]", false),
            "VK_OEM_7" => ("'", false),

            // 无修饰键的特殊情况
            "NONE" or "DISABLED" => ("(无)", false),

            _ => (keyName, false) // 无法识别则原样显示
        };
    }

    // ── 辅助方法 ────────────────────────────────────────

    /// <summary>段落类型 → 中文动作说明</summary>
    private static string SectionToAction(string section)
    {
        var name = section.Trim('[', ']');
        return name switch
        {
            "KeyHold" => "长按",
            "KeyHoldShape" => "长按切换",
            "KeyClickedSlot" => "点击切换",
            "KeyToggle" => "开/关",
            "KeySwap" => "切换",
            "KeySwitch" => "切换",
            "KeyCycle" => "循环",
            _ when name.StartsWith("KeySwap") => "切换",
            _ when name.StartsWith("KeyHold") => "长按",
            _ when name.StartsWith("KeyToggle") => "开/关",
            _ when name.StartsWith("Key") => "按键",
            _ => name
        };
    }

    private static string FriendlyKeyName(string keyValue)
    {
        var (display, _) = MapKeyName(keyValue);
        return display;
    }

    /// <summary>判断 key = value 中的值是否代表方向键</summary>
    private static bool IsArrowKeyValue(string keyValue)
    {
        var k = keyValue.Trim().ToUpperInvariant();
        return k is "↑" or "↓" or "←" or "→"
            or "VK_UP" or "VK_DOWN" or "VK_LEFT" or "VK_RIGHT"
            or "UP" or "DOWN" or "LEFT" or "RIGHT";
    }

    private static string CleanComment(string line)
    {
        return line.TrimStart(';').Trim().TrimStart(';', ' ').TrimEnd();
    }
}

public class ModIniKeyBindingGroup : INotifyPropertyChanged
{
    public string IniFileRelativePath { get; set; } = string.Empty;

    /// <summary>仅保留最后两层路径，用作 UI 小标题（如 "ModFolder/file.ini"）</summary>
    public string ShortPath
    {
        get
        {
            if (string.IsNullOrWhiteSpace(IniFileRelativePath))
                return string.Empty;

            var segments = IniFileRelativePath
                .Replace('\\', '/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries);

            return segments.Length <= 2
                ? IniFileRelativePath
                : string.Join("/", segments[^2..]);
        }
    }

    private bool _isExpanded = true;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            _isExpanded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsExpandedGlyph));
        }
    }

    /// <summary>折叠/展开箭头图标</summary>
    public string IsExpandedGlyph => _isExpanded ? "" : "";

    /// <summary>切换折叠状态（x:Bind 绑定到 Click 事件）</summary>
    public void ToggleExpanded() => IsExpanded = !IsExpanded;

    /// <summary>ini 文件完整绝对路径（用于打开文件）</summary>
    public string IniFileFullPath { get; set; } = string.Empty;

    /// <summary>用默认编辑器打开 ini 文件</summary>
    public void OpenIniFile()
    {
        if (!string.IsNullOrWhiteSpace(IniFileFullPath) && File.Exists(IniFileFullPath))
            Process.Start(new ProcessStartInfo
            {
                FileName = IniFileFullPath,
                UseShellExecute = true
            });
    }

    public List<ModIniKeyBindingEntry> Bindings { get; set; } = [];

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class ModIniKeyBindingEntry
{
    /// <summary>原始段落名，如 "[KeyHold]"</summary>
    public string SectionName { get; set; } = string.Empty;

    /// <summary>友好的按键显示，如 "Ctrl+Shift+H"、"→"、"🖱 左键"</summary>
    public string KeyValue { get; set; } = string.Empty;

    /// <summary>原始 ini 行（调试用），如 "no_ctrl no_shift VK_RIGHT"</summary>
    public string RawLine { get; set; } = string.Empty;

    /// <summary>动作说明，如 "长按"、"点击切换"、"开/关"</summary>
    public string ActionLabel { get; set; } = string.Empty;

    /// <summary>描述文本（从注释提取），如 "切换头发"</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>UI 显示的标签：去掉 "Key" 前缀的段名，如 "[KeySwapTextures]" → "SwapTextures"</summary>
    public string DisplayLabel
    {
        get
        {
            var name = SectionName.Trim('[', ']');
            if (name.StartsWith("Key", StringComparison.OrdinalIgnoreCase))
            {
                name = name[3..];
                if (name.StartsWith(' '))
                    name = name[1..];
            }
            return name;
        }
    }

    /// <summary>是否为方向键（UI 层用图标而非文字显示）</summary>
    public bool IsArrowKey { get; set; }
}
