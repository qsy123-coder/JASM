using GIMI_ModManager.Core.Entities.Mods.InGameMenu;

namespace GIMI_ModManager.Core.Services.OilGloss;

/// <summary>
/// 把油光补丁状态转成 F7 菜单的三行：总开关 / 身体 / 衣物。
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

    public static IReadOnlyList<ModIniToggleRow> Create(OilGlossManifest manifest)
    {
        // 没有命名空间时菜单无法限定变量，宁可不显示也不要生成一行点了没反应的开关
        if (string.IsNullOrWhiteSpace(manifest.Namespace))
            return [];

        var rows = new List<ModIniToggleRow>
        {
            CreateRow(MasterSection, OilGlossProtocol.MasterVariable, "油光总开关", "OilMaster", manifest.Namespace),
        };

        if (manifest.HasBodyGroup)
            rows.Add(CreateRow(BodySection, OilGlossProtocol.BodyVariable, "身体油光", "OilBody", manifest.Namespace));

        if (manifest.HasClothGroup)
            rows.Add(CreateRow(ClothSection, OilGlossProtocol.ClothVariable, "衣物油光", "OilCloth",
                manifest.Namespace, defaultValue: "0"));

        // 强度滑块：直接写本体命名空间里的 $oilStrength（0–1），拖到哪算到哪。
        rows.Add(new ModIniToggleRow
        {
            SectionName = StrengthSection,
            IniRelativePath = OilGlossProtocol.OilIniFileName,
            Namespace = OilGlossProtocol.BodyNamespace,
            VariableName = OilGlossProtocol.StrengthVariable,
            IsSlider = true,
            KeyType = "slider",
            DefaultValue = "0.7",
            Label = "油光强度",
            RawLabel = "OilStrength",
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