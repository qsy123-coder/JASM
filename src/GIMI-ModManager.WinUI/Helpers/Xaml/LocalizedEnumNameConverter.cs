using GIMI_ModManager.Core.Contracts.Services;
using Microsoft.UI.Xaml.Data;

namespace GIMI_ModManager.WinUI.Helpers.Xaml;

/// <summary>
/// 把枚举值显示成本地化文案：用 <c>ConverterParameter</c> 当词条前缀，查 <c>&lt;前缀&gt;_&lt;枚举名&gt;</c>。
/// 例：<c>SetModStatus.KeepCurrent</c> + <c>ConverterParameter="SetModStatus"</c> → 词条 <c>SetModStatus_KeepCurrent</c>。
/// </summary>
/// <remarks>
/// 直接绑枚举属性（现在导出对话框的下拉、「同步进程」状态徽章就是这么写的）会显示 <c>KeepCurrent</c> /
/// <c>NotRunning</c> 这种英文标识符，这个转换器把它们换成词条；当前语言缺词条时由
/// <see cref="ILanguageLocalizer"/> 回退 en-us，再查不到就用枚举名本身兜底（绝不显示空白）。
/// <para>
/// 本类是 XAML 渲染路径上的代码，任何异常都不能外抛，所以整体包在 try/catch 里 —— 这一点与
/// <see cref="EnumToBooleanConverter"/>「参数不对就抛」的风格相反，是有意为之。
/// </para>
/// </remarks>
public class LocalizedEnumNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        // 绑定还没求值时 value 为 null：给空串，别让它显示成 "null"
        if (value is null)
            return string.Empty;

        var enumName = value.ToString() ?? string.Empty;

        // 没给前缀就无从查词条，退回枚举名（至少让调用方看出是配置漏了）
        if (parameter is not string prefix || string.IsNullOrEmpty(prefix))
            return enumName;

        try
        {
            return App.GetService<ILanguageLocalizer>()
                       .GetLocalizedStringOrDefault($"{prefix}_{enumName}", defaultValue: enumName)
                   ?? enumName;
        }
        catch (Exception)
        {
            // App.Current 为空（XAML 设计器 / localizer 还没初始化）时 GetService 会抛，静默退回枚举名
            return enumName;
        }
    }

    /// <summary>只用于显示（OneWay）。被反向使用时直接报错，避免静默把字符串写回枚举属性。</summary>
    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException($"{nameof(LocalizedEnumNameConverter)} 只支持单向（OneWay）绑定。");
}