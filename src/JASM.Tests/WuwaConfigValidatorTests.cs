using GIMI_ModManager.Core.Services.WuwaModFixer;

namespace JASM.Tests;

/// <summary>
/// 配置校验的边界：宁可拒掉一份坏配置（保留旧的还能用），也不能放进来让 CLI 中途炸。
/// </summary>
public class WuwaConfigValidatorTests
{
    private const string ValidConfig = """
                                       {
                                         "version": {
                                           "min_required_version": "3.7.0",
                                           "current_version": "3.7.1"
                                         },
                                         "characters": {
                                           "Feixue": { "main_hashes": [] }
                                         }
                                       }
                                       """;

    [Fact]
    public void AcceptsValidConfigAndReturnsSummary()
    {
        var result = WuwaModFixerConfigValidator.Validate(ValidConfig, WuwaModFixerProtocol.CliVersion);

        Assert.True(result.IsValid);
        Assert.NotNull(result.Info);
        Assert.Equal("3.7.1", result.Info!.CurrentVersion);
        Assert.Equal("3.7.0", result.Info.MinRequiredVersion);
        Assert.Equal(1, result.Info.CharacterCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    public void RejectsInvalidJson(string json)
    {
        var result = WuwaModFixerConfigValidator.Validate(json, "3.7.0");

        Assert.False(result.IsValid);
        Assert.Equal(WuwaConfigValidationError.InvalidJson, result.Error);
    }

    [Theory]
    [InlineData("""{"version":{"min_required_version":"3.7.0","current_version":"3.7.1"},"characters":{}}""")]
    [InlineData("""{"version":{"min_required_version":"3.7.0","current_version":"3.7.1"},"characters":[]}""")]
    [InlineData("""{"version":{"min_required_version":"3.7.0","current_version":"3.7.1"}}""")]
    public void RejectsMissingCharacters(string json)
    {
        var result = WuwaModFixerConfigValidator.Validate(json, "3.7.0");

        Assert.False(result.IsValid);
        Assert.Equal(WuwaConfigValidationError.MissingCharacters, result.Error);
    }

    [Theory]
    [InlineData("""{"version":{"current_version":"3.7.1"},"characters":{"A":{}}}""")]
    [InlineData("""{"version":{"min_required_version":"3.7.0"},"characters":{"A":{}}}""")]
    [InlineData("""{"characters":{"A":{}}}""")]
    public void RejectsMissingVersionFields(string json)
    {
        var result = WuwaModFixerConfigValidator.Validate(json, "3.7.0");

        Assert.False(result.IsValid);
        Assert.Contains(result.Error,
            new[]
            {
                WuwaConfigValidationError.InvalidCurrentVersion,
                WuwaConfigValidationError.InvalidMinRequiredVersion
            });
    }

    [Fact]
    public void RejectsConfigRequiringNewerCli()
    {
        const string json = """
                            {
                              "version": {"min_required_version": "3.7.1", "current_version": "3.7.1"},
                              "characters": {"A": {}}
                            }
                            """;

        var result = WuwaModFixerConfigValidator.Validate(json, "3.7.0");

        Assert.False(result.IsValid);
        Assert.Equal(WuwaConfigValidationError.CliTooOld, result.Error);
    }

    [Theory]
    [InlineData("3.7.0")]
    [InlineData("3.6.9")]
    public void AcceptsConfigRequiringSameOrOlderCli(string minRequired)
    {
        var json = """
                   {
                     "version": {"min_required_version": "MIN", "current_version": "3.7.1"},
                     "characters": {"A": {}}
                   }
                   """.Replace("MIN", minRequired);

        Assert.True(WuwaModFixerConfigValidator.Validate(json, "3.7.0").IsValid);
    }
}