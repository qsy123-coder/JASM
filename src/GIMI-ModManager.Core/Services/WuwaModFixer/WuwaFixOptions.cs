namespace GIMI_ModManager.Core.Services.WuwaModFixer;

/// <summary>
/// 浮窗上的五个修复开关，与上游 CLI 的参数一一对应。
///
/// 基础「一键修复」没有开关：它是固定执行的，只有下面这些增强项可选。
/// 「补全贴图状态」与「应用稳定纹理」在上游 UI 里互斥（都改贴图引用，同开会让规则互相打架），
/// 互斥规则在这里兜底：即使设置文件被手改成两个 true，也只会有一个生效。
/// </summary>
public sealed record WuwaFixOptions(
    bool DerivedHashes = false,
    bool StableTexture = false,
    bool Rendering33 = false,
    bool AemeathMech = false,
    int AeroFixMode = 0)
{
    /// <summary>
    /// 归一化：Aero 模式夹到 0..2；两个互斥项同时为 true 时保留「应用稳定纹理」。
    ///
    /// 保留稳定纹理而不是补全贴图，是因为它作用面更窄、也更明确地被用户勾选；
    /// 谁先谁后不该影响结果，所以不按「最后点的那个」来定。
    /// </summary>
    public WuwaFixOptions Normalize()
    {
        var stable = StableTexture;
        var derived = DerivedHashes && !StableTexture;

        return this with
        {
            DerivedHashes = derived,
            StableTexture = stable,
            AeroFixMode = Math.Clamp(AeroFixMode, 0, 2)
        };
    }

    /// <summary>
    /// 转成 CLI 参数。互斥项最多出现一个；女漂修复只在 1/2 时出现。
    /// </summary>
    public IReadOnlyList<string> ToArguments()
    {
        var normalized = Normalize();
        var arguments = new List<string>(5);

        if (normalized.DerivedHashes)
            arguments.Add("--derived-hashes");

        if (normalized.StableTexture)
            arguments.Add("--stable-texture");

        if (normalized.Rendering33)
            arguments.Add("--rendering-33");

        if (normalized.AemeathMech)
            arguments.Add("--aemeath-mech");

        if (normalized.AeroFixMode is 1 or 2)
        {
            arguments.Add("--aero-fix");
            arguments.Add(normalized.AeroFixMode.ToString());
        }

        return arguments;
    }
}