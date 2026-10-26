using System.Text.Json;

namespace GIMI_ModManager.Core.Services.WuwaModFixer;

/// <summary>校验通过后的配置摘要，供界面展示与后续判断。</summary>
public sealed record WuwaConfigInfo(string CurrentVersion, string MinRequiredVersion, int CharacterCount);

/// <summary>配置校验失败的原因。界面按这些枚举给用户可读的下一步，而不是只显示「更新失败」。</summary>
public enum WuwaConfigValidationError
{
    None,
    InvalidJson,
    MissingCharacters,
    InvalidCurrentVersion,
    InvalidMinRequiredVersion,
    CliTooOld
}

/// <summary>校验结果。失败时 <see cref="Info"/> 为 null，<see cref="Message"/> 是给日志/提示用的原因。</summary>
public sealed record WuwaConfigValidationResult(
    bool IsValid,
    WuwaConfigInfo? Info,
    WuwaConfigValidationError Error,
    string? Message)
{
    public static WuwaConfigValidationResult Ok(WuwaConfigInfo info) =>
        new(true, info, WuwaConfigValidationError.None, null);

    public static WuwaConfigValidationResult Fail(WuwaConfigValidationError error, string message) =>
        new(false, null, error, message);
}

/// <summary>
/// 上游 <c>config.json</c> 的最小校验。
///
/// 只校验「用得上」的字段：角色表非空、两个版本号可解析、配置要求的最低程序版本不超过内置 CLI。
/// 上游随时会加字段，这里**不能**做严格 schema 校验，否则一次正常更新就会被我们拒之门外。
/// </summary>
public static class WuwaModFixerConfigValidator
{
    /// <summary>
    /// <paramref name="cliVersion"/> 用 <see cref="WuwaModFixerProtocol.CliVersion"/>。
    /// 配置的 <c>min_required_version</c> 高于它时，CLI 会拒绝这份配置，我们必须提前拦住。
    /// </summary>
    public static WuwaConfigValidationResult Validate(string? json, string cliVersion)
    {
        if (string.IsNullOrWhiteSpace(json))
            return WuwaConfigValidationResult.Fail(
                WuwaConfigValidationError.InvalidJson, "配置为空");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return WuwaConfigValidationResult.Fail(
                WuwaConfigValidationError.InvalidJson, "配置不是合法 JSON");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return WuwaConfigValidationResult.Fail(
                    WuwaConfigValidationError.InvalidJson, "配置根节点不是对象");

            if (!root.TryGetProperty("characters", out var characters)
                || !HasAnyCharacter(characters))
                return WuwaConfigValidationResult.Fail(
                    WuwaConfigValidationError.MissingCharacters, "配置缺少角色表");

            if (!root.TryGetProperty("version", out var version)
                || version.ValueKind != JsonValueKind.Object)
                return WuwaConfigValidationResult.Fail(
                    WuwaConfigValidationError.InvalidCurrentVersion, "配置缺少版本信息");

            var currentVersion = GetString(version, "current_version");
            if (!WuwaVersion.TryParse(currentVersion, out _))
                return WuwaConfigValidationResult.Fail(
                    WuwaConfigValidationError.InvalidCurrentVersion, "配置的 current_version 无效");

            var minRequiredVersion = GetString(version, "min_required_version");
            if (!WuwaVersion.TryParse(minRequiredVersion, out _))
                return WuwaConfigValidationResult.Fail(
                    WuwaConfigValidationError.InvalidMinRequiredVersion, "配置的 min_required_version 无效");

            if (WuwaVersion.Compare(minRequiredVersion, cliVersion) > 0)
                return WuwaConfigValidationResult.Fail(
                    WuwaConfigValidationError.CliTooOld,
                    $"配置要求程序 {minRequiredVersion}，当前内置 {cliVersion}");

            var characterCount = characters.ValueKind == JsonValueKind.Object
                ? characters.EnumerateObject().Count()
                : characters.GetArrayLength();

            return WuwaConfigValidationResult.Ok(
                new WuwaConfigInfo(currentVersion!.Trim(), minRequiredVersion!.Trim(), characterCount));
        }
    }

    /// <summary>角色表可以是对象（当前上游形态）或数组；两种都算有内容。</summary>
    private static bool HasAnyCharacter(JsonElement characters) =>
        characters.ValueKind switch
        {
            JsonValueKind.Object => characters.EnumerateObject().Any(),
            JsonValueKind.Array => characters.GetArrayLength() > 0,
            _ => false
        };

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}