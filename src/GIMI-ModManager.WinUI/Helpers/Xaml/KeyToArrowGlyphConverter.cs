using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace GIMI_ModManager.WinUI.Helpers.Xaml;

/// <summary>
/// 将友好的按键显示文本转换为 Segoe Fluent 箭头图标 Glyph。
/// 箭头字符 → 对应图标；非箭头 → 空字符串。
/// </summary>
internal class KeyToArrowGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not string key)
            return string.Empty;

        // KeyValue 已经是格式化后的友好文本，如 "→"、"Ctrl+Shift+H"、"🖱 左键"
        // 只对箭头字符返回对应 glyph
        return key.Trim() switch
        {
            "→" => "", // RightArrow
            "←" => "", // LeftArrow
            "↑" => "", // UpArrow
            "↓" => "", // DownArrow
            _ => string.Empty
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

/// <summary>IsArrowKey=true → 隐藏文字徽章</summary>
internal class ArrowKeyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is bool isArrow && isArrow)
            return Visibility.Collapsed;
        return Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

/// <summary>IsArrowKey=true → 显示箭头图标</summary>
internal class ArrowKeyToIconVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is bool isArrow && isArrow)
            return Visibility.Visible;
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
