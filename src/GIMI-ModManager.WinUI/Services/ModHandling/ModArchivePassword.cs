namespace GIMI_ModManager.WinUI.Services.ModHandling;

/// <summary>
/// Mod 压缩包的内置密码。
///
/// <para>
/// <b>为什么是写死的常量</b>：用户决策（2026-10-01）—— 国内社区分发的 Mod 包用的就是同一个密码，
/// 而且不会变，所以直接内置、解压时自动带上，<b>不再弹框问用户</b>，也不往设置里存。
/// 原先那套「弹框 + 记住」已经被这个常量取代（密码对话框连同 <c>ArchivePasswordService</c> 一起删掉了）。
/// </para>
///
/// <para>
/// <b>硬约束：这个值不得出现在任何用户可见的地方</b> —— 日志、异常消息、通知文案、诊断串都不行。
/// 取命令行进日志务必走 <c>DragAndDropScanner.RedactCommand</c>（它会把 <c>-p{密码}</c> 打成 <c>-p***</c>）。
/// </para>
///
/// <para>
/// 换密码就改这里一处；包用的不是这个密码时，解压会以
/// <see cref="GIMI_ModManager.Core.Helpers.ArchiveExtractionFailureReason.WrongPassword"/> 失败，
/// 界面上给的是「自己解压好再拖文件夹」的出路（见 <c>CharactersViewModel.ShowExtractionFailureNotification</c>）。
/// </para>
/// </summary>
public static class ModArchivePassword
{
    /// <summary>社区分发的 Mod 包通用密码。</summary>
    public const string Default = "x77syq";
}