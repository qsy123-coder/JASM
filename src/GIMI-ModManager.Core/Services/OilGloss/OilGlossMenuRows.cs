using System.Globalization;
using GIMI_ModManager.Core.Entities.Mods.InGameMenu;

namespace GIMI_ModManager.Core.Services.OilGloss;

/// <summary>
/// 把油光补丁状态转成 F7 菜单的两行：油光开关 / 油光强度。
///
/// 这些行不写进任何 ini，而是通过 <see cref="InGameMenuOptions.ExtraRows"/> 直接注入生成器：
/// 变量的命名空间、候选值、默认值都由 manifest 决定，标签用中文，顺序固定在最前面。
/// </summary>
public static class OilGlossMenuRows
{
    public const string MasterSection = "[KeyOilMaster]";
    public const string BodySection = "[KeyOilBody]";
    public const string ClothSection = "[KeyOilCloth]";
    public const string StrengthSection = "[KeyOilStrength]";
    public const string TextureSection = "[KeyOilTexture]";

    public static IReadOnlyList<ModIniToggleRow> Create(OilGlossManifest manifest)
    {
        // 没有命名空间时菜单无法限定变量，宁可不显示也不要生成一行点了没反应的开关
        if (string.IsNullOrWhiteSpace(manifest.Namespace))
            return [];

        // 只有一个总开关 + 强度：官方做法按 drawcall 包，同一个 drawcall 常同时包含
        // 身体和衣物，再拆成两个开关只会出现「关了衣物身体还亮」的假开关。
        var rows = new List<ModIniToggleRow>
        {
            CreateRow(MasterSection, OilGlossProtocol.MasterVariable, "油光", "OilMaster", manifest.Namespace),
        };

        // 强度滑块：直接写本体命名空间里的 $oilStrength，拖到哪算到哪。
        // 上限由本体支持到 2.0（大于 1 会更亮），菜单轨道按 SliderMax 归一化。
        rows.Add(new ModIniToggleRow
        {
            SectionName = StrengthSection,
            IniRelativePath = OilGlossProtocol.OilIniFileName,
            Namespace = OilGlossProtocol.BodyNamespace,
            VariableName = OilGlossProtocol.StrengthVariable,
            IsSlider = true,
            SliderMax = OilGlossProtocol.MaxStrength,
            KeyType = "slider",
            DefaultValue = OilGlossProtocol.DefaultStrength.ToString("0.###", CultureInfo.InvariantCulture),
            Label = "油光强度",
            RawLabel = "OilStrength",
        });

        // 贴图样式：本体 $oilSkin 的分支 1–6 对应 6 张 matcap 贴图。
        // 0 是「关」，交给上面的总开关，不放进来避免出现第二个关油光的入口。
        rows.Add(new ModIniToggleRow
        {
            SectionName = TextureSection,
            IniRelativePath = OilGlossProtocol.OilIniFileName,
            Namespace = OilGlossProtocol.BodyNamespace,
            VariableName = OilGlossProtocol.TextureVariable,
            Values = ["2", "3", "4", "5", "6", "1"],
            ValueLabels = ["MC6", "MC1", "Oil1", "Oil2", "Wet", "OIl_MC"],
            DefaultValue = "2",
            KeyType = "cycle",
            KeyDisplay = string.Empty,
            Condition = string.Empty,
            Label = "油光贴图",
            RawLabel = "OilTexture",
        });

        return rows;
    }

    private static ModIniToggleRow CreateRow(
        string section,
        string variable,
        string label,
        string rawLabel,
        string ns,
        string defaultValue = "1") => new()
        {
            SectionName = section,
            IniRelativePath = OilGlossProtocol.OilIniFileName,
            Namespace = ns,
            VariableName = variable,
            Values = ["0", "1"],
            DefaultValue = defaultValue,
            KeyType = "cycle",
            KeyDisplay = string.Empty,
            Condition = string.Empty,
            Label = label,
            RawLabel = rawLabel,
        };
}