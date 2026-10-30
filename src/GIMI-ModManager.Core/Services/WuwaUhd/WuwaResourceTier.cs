namespace GIMI_ModManager.Core.Services.WuwaUhd;

/// <summary>
/// 鸣潮的资源档位。游戏通过启动参数 <c>-krqlv=SD|HD|UHD</c> 选择资源包；
/// 不同档位下同一张贴图的 hash 可能不同，mod 的贴图引用要按当前档位修。
///
/// <see cref="Auto"/> 表示“不指定”——启动时交给官方启动器/XXMI 决定，配置修复时也不写档位相关项。
/// </summary>
public enum WuwaResourceTier
{
    Auto = 0,
    Sd = 1,
    Hd = 2,
    Uhd = 3
}

public static class WuwaResourceTierExtensions
{
    /// <summary>
    /// 转成游戏启动参数。Auto 返回 <c>null</c>（不加参数）。
    ///
    /// 注意：这是社区/上游工具（WuWa-Configs）使用的参数名，不是从 Rocket 里直接读到的字符串；
    /// 若游戏版本变更导致参数失效，应在这里单点修正。
    /// </summary>
    public static string? ToLaunchArgument(this WuwaResourceTier tier) => tier switch
    {
        WuwaResourceTier.Sd => "-krqlv=SD",
        WuwaResourceTier.Hd => "-krqlv=HD",
        WuwaResourceTier.Uhd => "-krqlv=UHD",
        _ => null
    };

    public static string ToDisplayName(this WuwaResourceTier tier) => tier switch
    {
        WuwaResourceTier.Sd => "流畅 SD",
        WuwaResourceTier.Hd => "高清 HD",
        WuwaResourceTier.Uhd => "极致 UHD",
        _ => "自动识别"
    };

    /// <summary>把浮窗里的下拉序号（0..3）转成档位；越界一律 Auto。</summary>
    public static WuwaResourceTier FromIndex(int index) => index switch
    {
        1 => WuwaResourceTier.Sd,
        2 => WuwaResourceTier.Hd,
        3 => WuwaResourceTier.Uhd,
        _ => WuwaResourceTier.Auto
    };

    public static int ToIndex(this WuwaResourceTier tier) => (int)tier;
}
