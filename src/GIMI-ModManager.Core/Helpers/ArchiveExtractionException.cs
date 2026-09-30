namespace GIMI_ModManager.Core.Helpers;

/// <summary>解压失败的具体原因。UI 层按它挑一条**本地化**文案，而不是直接把异常消息显示给用户。</summary>
public enum ArchiveExtractionFailureReason
{
    /// <summary>这压根不是 JASM 能识别的 Mod 包（普通程序、安装器、随便一个文件）。</summary>
    NotAnArchive,

    /// <summary>是加密包，但本侧没传密码。</summary>
    NeedsPassword,

    /// <summary>传了密码，但密码不对。</summary>
    WrongPassword,

    /// <summary>包损坏 / 解压中断。</summary>
    Corrupt,

    /// <summary>解压跑完了，但一个文件都没出来（半失败，不能当成功放过去）。</summary>
    EmptyOutput,

    /// <summary>工具本身跑不起来或报了我们没预期的错（内置 7z 缺失 / 被杀软拦 / 参数错）。</summary>
    ToolFailed
}

/// <summary>
/// 解压失败时抛出的类型化异常。
///
/// <para>
/// 之所以要类型化：加密包的密码问题与「包坏了」在 7-Zip 的输出里长得几乎一样，而两者对用户
/// 要做的事完全不同（一个是「再输一次密码」，一个是「重新下载」）。把原因编码进异常，UI 层才能
/// 分别提示，也让这两条路都**不会静默**地留下一个空文件夹。
/// </para>
///
/// <para>
/// <b>约定：<see cref="Exception.Message"/> 一律是开发者可读的英文，且绝不含密码。</b>
/// 调用方要展示给用户时，请按 <see cref="Reason"/> 取本地化文案。
/// </para>
/// </summary>
public sealed class ArchiveExtractionException : Exception
{
    public ArchiveExtractionException(ArchiveExtractionFailureReason reason, string message,
        Exception? innerException = null) : base(message, innerException)
    {
        Reason = reason;
    }

    public ArchiveExtractionFailureReason Reason { get; }
}
