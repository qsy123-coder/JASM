namespace GIMI_ModManager.Core.Helpers;

/// <summary>
/// 把 7-Zip 的「退出码 + stdout + stderr」翻译成 <see cref="ArchiveExtractionFailureReason"/>。
///
/// <para>
/// 纯函数（不碰进程、不碰磁盘），所以能拿真实抓到的 7-Zip 输出直接喂进单测 —— 判据的每条分支
/// 都有实测样例，见下。**没把握的别再靠猜**：7-Zip 的两条消息文案是它内置的英文串，不受系统
/// 语言影响（对比：<c>System ERROR:</c> 那类由 OS 提供、会随语言变，所以一概不匹配）。
/// </para>
///
/// <para>实测样例（7-Zip 24.09，stdin 关闭）：</para>
/// <list type="bullet">
/// <item><c>x plain.txt</c> → exit 2，<c>ERROR: ... : Cannot open the file as archive</c></item>
/// <item><c>x enc_h.7z</c>（头加密，无密码或密码错）→ exit 2，<c>Cannot open encrypted archive. Wrong password?</c></item>
/// <item><c>x enc.7z</c>（数据加密，无密码或密码错）→ exit 2，<c>ERROR: Data Error in encrypted file. Wrong password? : a.txt</c></item>
/// <item><c>x plain.7z</c>（密码正确）→ exit 0</item>
/// <item><c>x 7z.exe</c>（普通 PE）→ exit 0 且抽出 7 个「文件」 ⇒ **退出码 0 不代表它是个 Mod 包**，
/// 所以这条判据只用来判「这次解压有没有读出东西」，是不是压缩包另由
/// <see cref="SfxPayloadDetector"/> 在解压**之前**把关。</item>
/// </list>
/// </summary>
public static class ArchiveExtractionClassifier
{
    /// <summary>
    /// 7-Zip 的「密码不对 / 没给密码」消息里共有的那段。头加密与数据加密两种文案都含它，
    /// 且**二者输出完全一样** —— 所以「到底是要密码还是密码错」只能由调用方按
    /// <paramref name="passwordSupplied"/> 区分，不能从输出里读。
    /// </summary>
    private const string PasswordNeedle = "Wrong password?";

    /// <summary>7-Zip 认不出这是压缩包时的消息。</summary>
    private const string NotAnArchiveNeedle = "Cannot open the file as archive";

    /// <summary>7-Zip 的退出码：0 成功、1 警告（有文件没处理）、2 致命、255 被用户中止、7 命令行错、8 内存不足。</summary>
    public const int SuccessExitCode = 0;

    public const int WarningExitCode = 1;
    public const int FatalExitCode = 2;

    /// <summary>这次解压算不算成功。退出码 1 是「警告」，操作本身完成了，按成功处理（调用方另行记日志）。</summary>
    public static bool IsSuccess(int exitCode) => exitCode is SuccessExitCode or WarningExitCode;

    /// <summary>
    /// 判定失败原因。成功返回 <c>null</c>。
    /// </summary>
    /// <param name="exitCode">7-Zip 退出码。</param>
    /// <param name="stdout">标准输出（已解码）。</param>
    /// <param name="stderr">标准错误（已解码）。</param>
    /// <param name="passwordSupplied">本次调用**本侧有没有传 <c>-p</c></b>。传过还报密码错 = 密码错；没传过 = 需要密码。</param>
    public static ArchiveExtractionFailureReason? Classify(int exitCode, string? stdout, string? stderr,
        bool passwordSupplied)
    {
        if (IsSuccess(exitCode))
            return null;

        var text = string.Concat(stderr, "\n", stdout);

        // 密码问题优先判：它和「包坏了」在退出码上都是 2，只能靠文案区分。
        if (text.Contains(PasswordNeedle, StringComparison.Ordinal))
            return passwordSupplied
                ? ArchiveExtractionFailureReason.WrongPassword
                : ArchiveExtractionFailureReason.NeedsPassword;

        if (text.Contains(NotAnArchiveNeedle, StringComparison.Ordinal))
            return ArchiveExtractionFailureReason.NotAnArchive;

        return exitCode == FatalExitCode
            ? ArchiveExtractionFailureReason.Corrupt
            : ArchiveExtractionFailureReason.ToolFailed;
    }
}
