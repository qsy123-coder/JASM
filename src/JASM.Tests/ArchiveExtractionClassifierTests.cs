using GIMI_ModManager.Core.Helpers;

namespace JASM.Tests;

/// <summary>
/// Covers <see cref="ArchiveExtractionClassifier"/> —— 7-Zip 的输出怎么翻译成「为什么失败」。
///
/// 用例里的 stdout / stderr **是从真 7-Zip 24.09 抄下来的**（命令与样本见 <see cref="ArchiveExtractionClassifier"/>
/// 的类注释）。这里最要紧的一条是：**「没给密码」与「密码错了」的输出一模一样**，
/// 只能由调用方是否传过 <c>-p</c> 区分 —— 判错的后果是用户明明输对了密码却被反复索要。
/// </summary>
public class ArchiveExtractionClassifierTests
{
    // 实测原文（7z x <头加密的 7z>，无密码 / 密码错 —— 两者逐字相同）
    private const string HeaderEncryptedError =
        "ERROR: C:\\Temp\\enc_h.7z\nCannot open encrypted archive. Wrong password?\n\nERRORS:\nHeaders Error\n";

    // 实测原文（7z x <数据加密的 7z>，无密码 / 密码错）
    private const string DataEncryptedError =
        "ERROR: Data Error in encrypted file. Wrong password? : a.txt\n";

    // 实测原文（7z x plain.txt）
    private const string NotAnArchiveError =
        "ERROR: C:\\Temp\\plain.txt : Cannot open the file as archive\n";

    [Fact]
    public void ASuccessfulExtractionHasNoFailureReason()
    {
        Assert.Null(ArchiveExtractionClassifier.Classify(0, "Everything is Ok\n", "", passwordSupplied: false));
    }

    [Fact]
    public void AWarningIsTreatedAsSuccess()
    {
        // 退出码 1 = 「有文件没处理」，操作本身完成了。Mod 里少一两个可选文件不该让整次安装失败，
        // 调用方另行记 Warning
        Assert.True(ArchiveExtractionClassifier.IsSuccess(1));
        Assert.Null(ArchiveExtractionClassifier.Classify(1, "", "WARNING: Cannot set attribute\n", false));
    }

    [Fact]
    public void ASuccessIsASuccessEvenIfTheOutputMentionsWrongPassword()
    {
        // 不能一见到 "Wrong password?" 就判失败：退出码已经说了这次是成功的
        Assert.Null(ArchiveExtractionClassifier.Classify(0, "Wrong password? whatever\n", "", false));
    }

    [Theory]
    [InlineData(HeaderEncryptedError)]
    [InlineData(DataEncryptedError)]
    public void AMissingPasswordIsReportedAsNeedingOne(string error)
    {
        Assert.Equal(ArchiveExtractionFailureReason.NeedsPassword,
            ArchiveExtractionClassifier.Classify(2, "", error, passwordSupplied: false));
    }

    [Theory]
    [InlineData(HeaderEncryptedError)]
    [InlineData(DataEncryptedError)]
    public void AWrongPasswordIsReportedAsWrongWhenOneWasSupplied(string error)
    {
        Assert.Equal(ArchiveExtractionFailureReason.WrongPassword,
            ArchiveExtractionClassifier.Classify(2, "", error, passwordSupplied: true));
    }

    [Fact]
    public void AFileThatIsNotAnArchiveIsReportedAsSuch()
    {
        Assert.Equal(ArchiveExtractionFailureReason.NotAnArchive,
            ArchiveExtractionClassifier.Classify(2, "", NotAnArchiveError, passwordSupplied: false));
    }

    [Fact]
    public void ADamagedArchiveIsReportedAsCorrupt()
    {
        // 退出码 2 + 我们不认识的文案 = 包坏了（也可能是 7z 报了个新错，但那同样不该当成「成功」）
        Assert.Equal(ArchiveExtractionFailureReason.Corrupt,
            ArchiveExtractionClassifier.Classify(2, "", "ERRORS:\nData Error in encrypted file. : a.txt\n", true));
    }

    [Fact]
    public void AnOsLevelErrorIsNotMistakenForAPasswordProblem()
    {
        // 「系统找不到指定的文件」这类由 OS 提供的消息**会随系统语言变**（实测中文机器上抓到的是中文），
        // 所以一概不匹配它们。这里用中英各一份确保都没被误判成密码问题
        Assert.Equal(ArchiveExtractionFailureReason.Corrupt,
            ArchiveExtractionClassifier.Classify(2, "", "ERROR: 系统找不到指定的文件。\nSystem ERROR:\n", true));
        Assert.Equal(ArchiveExtractionFailureReason.Corrupt,
            ArchiveExtractionClassifier.Classify(2, "", "ERROR: The system cannot find the file specified.\n", true));
    }

    [Theory]
    [InlineData(7)] // 命令行错误
    [InlineData(8)] // 内存不足
    [InlineData(255)] // 被中止
    public void AnUnexpectedExitCodeIsAToolFailure(int exitCode)
    {
        Assert.Equal(ArchiveExtractionFailureReason.ToolFailed,
            ArchiveExtractionClassifier.Classify(exitCode, "", "", false));
    }

    [Fact]
    public void ClassificationSurvivesNullOutput()
    {
        Assert.Equal(ArchiveExtractionFailureReason.ToolFailed,
            ArchiveExtractionClassifier.Classify(255, null, null, false));
    }
}
