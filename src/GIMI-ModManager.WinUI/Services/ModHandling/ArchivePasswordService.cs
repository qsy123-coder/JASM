using GIMI_ModManager.Core.Contracts.Services;
using GIMI_ModManager.Core.Helpers;
using GIMI_ModManager.Core.Services;
using GIMI_ModManager.WinUI.Contracts.Services;
using GIMI_ModManager.WinUI.Models.Settings;
using GIMI_ModManager.WinUI.Services.AppManagement;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog;

namespace GIMI_ModManager.WinUI.Services.ModHandling;

/// <summary>
/// 加密 Mod 压缩包的密码：读回来 → 拿它试 → 不行就问用户 → 问到了记住。
///
/// <para>
/// <b>为什么只有一条全局密码</b>：用户决策 —— 社区分发的 Mod 包密码基本是同一个，
/// 所以设置里只留一条（<see cref="ModArchiveSettings.ArchivePassword"/>），不做密码簿、不按作者分管理、不做字典猜。
/// </para>
///
/// <para>
/// <b>密码不得出现在任何地方</b>（日志 / 异常消息 / 通知 / 诊断串）—— 本功能的硬约束。
/// 这里只把它当内存里的字符串传递。落盘那一份由 <see cref="ModArchiveSettings"/> 负责
/// （明文、本机设置文件，已与用户确认过）。
/// </para>
///
/// <para>
/// 对话框是<b>代码搭的</b>（照 <c>ModRandomizationService</c> 的形态），不是 XAML 定义的 ContentDialog：
/// 它一共三行控件，为它新建 xaml + xaml.cs + ViewModel 三个文件不划算，也省掉 uid 本地化那一套坑。
/// </para>
/// </summary>
public sealed class ArchivePasswordService
{
    private readonly ILocalSettingsService _localSettingsService;
    private readonly IWindowManagerService _windowManagerService;
    private readonly ILogger _logger;
    private readonly ILanguageLocalizer _localizer = App.GetService<ILanguageLocalizer>();

    public ArchivePasswordService(ILocalSettingsService localSettingsService,
        IWindowManagerService windowManagerService, ILogger logger)
    {
        _localSettingsService = localSettingsService;
        _windowManagerService = windowManagerService;
        _logger = logger.ForContext<ArchivePasswordService>();
    }

    /// <summary>用户记住的密码；没记住过就是 <c>null</c>。</summary>
    public string? GetRememberedPassword() =>
        _localSettingsService.ReadSetting<ModArchiveSettings>(ModArchiveSettings.Key)?.ArchivePassword;

    /// <summary>
    /// 清掉记住的密码。密码对话框里那个「清除已记住的密码」按钮走这里 ——
    /// 设置页没有对应的 UI（用户决策：密码相关的一切只在这一处）。
    /// </summary>
    public Task ClearRememberedPasswordAsync() => SetRememberedPasswordAsync(null);

    /// <summary>
    /// 解压，遇到密码问题就弹框问用户，问到了再试一次。
    /// 返回 <c>null</c> = 用户放弃（取消对话框，或已有别的对话框挡着没法问）。
    /// </summary>
    /// <param name="extract">
    /// 真正干活的解压动作，入参是这次要用的密码（<c>null</c> = 不带密码）。调用方一般传
    /// <c>password =&gt; scanner.ScanAndGetContents(path, password)</c>。
    ///
    /// <para>
    /// 密码错时会被调用第二次，解压目标还是同一个工作目录 —— 靠 7-Zip 的 <c>-y</c> 覆盖。
    /// 头加密的包第一次什么都没解出来，数据加密的包可能留下几个半截文件，重试时会盖掉。
    /// </para>
    /// </param>
    /// <exception cref="ArchiveExtractionException">
    /// 与密码无关的失败（不是压缩包 / 包坏了 / 内置 7-Zip 起不来）原样抛出，
    /// 由调用方按 <see cref="ArchiveExtractionException.Reason"/> 给用户相应的话。
    /// </exception>
    public async Task<DragAndDropScanResult?> ExtractAsync(Func<string?, DragAndDropScanResult> extract)
    {
        var password = GetRememberedPassword();

        while (true)
        {
            try
            {
                var result = extract(password);

                // 装成功才记住：这条密码确实开得了这个包。（本来就是记住的那条不用重复写盘）
                await RememberIfNewAsync(password);

                return result;
            }
            catch (ArchiveExtractionException e) when (
                e.Reason is ArchiveExtractionFailureReason.NeedsPassword
                    or ArchiveExtractionFailureReason.WrongPassword)
            {
                // 「刚才那条密码被拒了」这个判断只能由我们自己做：7-Zip 对「没给密码」与「密码错了」
                // 输出**逐字相同**（都是 Wrong password?），退出码也一样 —— 只有调用方知道有没有传过 -p。
                var rejected = password;

                password = await PromptForPasswordAsync(passwordWasRejected: !string.IsNullOrEmpty(rejected));
                if (password is null)
                    return null; // 用户放弃
            }
        }
    }

    /// <summary>
    /// 弹密码框。返回用户输入的密码；用户取消（或对话框根本弹不起来）返回 <c>null</c>。
    /// </summary>
    /// <param name="passwordWasRejected">
    /// 上一条密码是不是「传了但被拒」—— 决定提示语是「请输入密码」还是「密码不正确」，
    /// 以及要不要把上一条预填进框里（密码错多半是打错一个字，从头敲一遍很烦）。
    /// </param>
    private async Task<string?> PromptForPasswordAsync(bool passwordWasRejected)
    {
        var remembered = GetRememberedPassword();

        var hint = new TextBlock
        {
            Text = passwordWasRejected
                ? _localizer.GetLocalizedStringOrDefault("ArchivePassword_WrongHint",
                    defaultValue: "That password did not work. Please try again.")
                : _localizer.GetLocalizedStringOrDefault("ArchivePassword_NeedsHint",
                    defaultValue: "This archive is encrypted. Enter its password to continue."),
            TextWrapping = TextWrapping.Wrap
        };

        var passwordBox = new PasswordBox
        {
            PlaceholderText = _localizer.GetLocalizedStringOrDefault("ArchivePassword_Placeholder",
                defaultValue: "Password"),
            // 预填上一条，方便改错字。显隐模式用控件自带的 Peek（按住小眼睛才显形），不改成常显。
            Password = remembered ?? string.Empty,
            Margin = new Thickness(0, 10, 0, 0)
        };

        var clearButton = new Button
        {
            Content = _localizer.GetLocalizedStringOrDefault("ArchivePassword_ClearBtn",
                defaultValue: "Forget saved password"),
            Margin = new Thickness(0, 10, 0, 0),
            IsEnabled = !string.IsNullOrEmpty(remembered) // 没记住过就没得清
        };

        var note = new TextBlock
        {
            Text = _localizer.GetLocalizedStringOrDefault("ArchivePassword_Note",
                defaultValue: "The password is saved in this app's local settings and reused next time."),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0)
        };

        var content = new StackPanel();
        content.Children.Add(hint);
        content.Children.Add(passwordBox);
        content.Children.Add(clearButton);
        content.Children.Add(note);

        var dialog = new ContentDialog
        {
            // XamlRoot 不在这里设：ShowDialogAsync 自己会设（还会带上门互斥的检查）
            Title = passwordWasRejected
                ? _localizer.GetLocalizedStringOrDefault("ArchivePassword_WrongTitle",
                    defaultValue: "Wrong password")
                : _localizer.GetLocalizedStringOrDefault("ArchivePassword_NeedsTitle",
                    defaultValue: "Password required"),
            PrimaryButtonText = _localizer.GetLocalizedStringOrDefault("ArchivePassword_ConfirmBtn",
                defaultValue: "OK"),
            CloseButtonText = _localizer.GetLocalizedStringOrDefault("ArchivePassword_CancelBtn",
                defaultValue: "Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            Content = content,
            IsPrimaryButtonEnabled = passwordBox.Password.Length > 0
        };

        passwordBox.PasswordChanged += (_, _) =>
            dialog.IsPrimaryButtonEnabled = passwordBox.Password.Length > 0;

        clearButton.Click += async (_, _) =>
        {
            try
            {
                await ClearRememberedPasswordAsync();
            }
            catch (Exception e)
            {
                // 清不掉就当没点过（按钮还亮着，用户可以再点一次）——
                // 别因为这点小事把密码框整个关掉，那样用户连输密码都没机会
                _logger.Warning(e, "Failed to clear the remembered archive password");
                return;
            }

            // 框里那条是刚被清掉的坏密码，留着会让用户误以为还能用
            passwordBox.Password = string.Empty;
            hint.Text = _localizer.GetLocalizedStringOrDefault("ArchivePassword_ClearedHint",
                defaultValue: "The saved password was cleared. Enter the password");

            clearButton.IsEnabled = false; // 已经清干净了，再点没有意义
        };

        dialog.Opened += (_, _) => passwordBox.Focus(FocusState.Programmatic);

        ContentDialogResult result;
        try
        {
            result = await _windowManagerService.ShowDialogAsync(dialog);
        }
        catch (InvalidOperationException e)
        {
            // 同一窗口已经有别的对话框开着（ShowDialogAsync 的守卫）。这时**不能**当成「用户输错了」，
            // 更不能替他拿旧密码再试一遍 —— 老实放弃，让调用方安静退出。
            _logger.Warning(e, "Could not show the archive password dialog because another dialog is open");
            return null;
        }

        if (result != ContentDialogResult.Primary || passwordBox.Password.Length == 0)
            return null;

        return passwordBox.Password;
    }

    /// <summary>
    /// 这次用的密码不是记住的那条就存下来（用户新输的密码下次自动生效）；
    /// 本来就是记住的那条不用重复写盘。
    /// </summary>
    private async Task RememberIfNewAsync(string? password)
    {
        if (string.IsNullOrEmpty(password)) return;
        if (string.Equals(password, GetRememberedPassword(), StringComparison.Ordinal)) return;

        try
        {
            await SetRememberedPasswordAsync(password);
        }
        catch (Exception e)
        {
            // 「记住密码」是顺手的便利，写失败不该让这次已经成功的解压跟着失败。
            // 异常里带的是设置文件的路径，不是密码。
            _logger.Warning(e, "Failed to remember the archive password");
        }
    }

    private async Task SetRememberedPasswordAsync(string? password)
    {
        var settings = await _localSettingsService
            .ReadOrCreateSettingAsync<ModArchiveSettings>(ModArchiveSettings.Key);

        settings.ArchivePassword = password;

        await _localSettingsService.SaveSettingAsync(ModArchiveSettings.Key, settings);
    }
}