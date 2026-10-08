using Windows.System;
using CommunityToolkitWrapper;
using GIMI_ModManager.Core.GamesService;
using GIMI_ModManager.WinUI.Contracts.Services;
using GIMI_ModManager.WinUI.Helpers;
using GIMI_ModManager.WinUI.Services;
using GIMI_ModManager.WinUI.Services.AppManagement;
using GIMI_ModManager.WinUI.ViewModels;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace GIMI_ModManager.WinUI.Views;

public sealed partial class ShellPage : Page
{
    public ShellViewModel ViewModel { get; }

    public ShellPage(ShellViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        // 窗口根挂一份拖拽探针：各页面的探针只在事件真的到页面时才说话，而「事件压根没进进程」
        // （跨完整性级别的拖拽被 UIPI 掐掉就是这样）只有这里看得见 —— 收「拖不进去」的日志时，
        // 这一行有没有，直接决定该查权限还是该查落点。见 Helpers/DragProbe。
        // 只记日志、**不设 AcceptedOperation**：光标行为与挂之前一模一样（没落点的地方仍是禁止）。
        if (Content is UIElement probeRoot)
        {
            probeRoot.AllowDrop = true;
            probeRoot.AddHandler(UIElement.DragEnterEvent,
                new DragEventHandler((_, e) => DragProbe.Log("窗口根/DragEnter", e)), true);
            probeRoot.AddHandler(UIElement.DragOverEvent,
                new DragEventHandler((_, e) => DragProbe.Log("窗口根/DragOver", e)), true);
        }

        ViewModel.NavigationService.Frame = NavigationFrame;
        ViewModel.NavigationViewService.Initialize(NavigationViewControl);

        NavigationViewControl.IsPaneOpen = false;


        // TODO: Set the title bar icon by updating /Assets/WindowIcon.ico.
        // A custom title bar is required for full window theme and Mica support.
        // https://docs.microsoft.com/windows/apps/develop/title-bar?tabs=winui3#full-customization
        App.MainWindow.ExtendsContentIntoTitleBar = true;
        App.MainWindow.SetTitleBar(AppTitleBar);
        App.MainWindow.Activated += MainWindow_Activated;
        AppTitleBarText.Text = ResourceExtensions.GetLocalized("AppDisplayName");
#if DEBUG
        AppTitleBarText.Text += " - DEBUG";
#endif
        KeyDown += GlobalKeyHandler_Invoked;
        PointerPressed += GlobalMouseHandler_Invoked;

        Loaded += (sender, args) =>
        {
            ShowElevationNoticeIfDragDropIsBlocked();

            var bindings = new Binding()
            {
                Source = ViewModel,
                Path = new PropertyPath(nameof(ViewModel.SettingsInfoBadgeOpacity)),
                Mode = BindingMode.OneWay
            };

            var settingsItem = (NavigationViewItem)NavigationViewControl.SettingsItem;
            var infoBadge = new InfoBadge() { Opacity = 0, Value = 1 };
            settingsItem.InfoBadge = infoBadge;


            BindingOperations.SetBinding(settingsItem.InfoBadge, OpacityProperty, bindings);

            Bindings.Update();
        };

        ViewModel.GameService.Initialized += GameServiceOnInitialized;

#if RELEASE
        // Hide debug menu in release mode
        DebugItem.Visibility = Visibility.Collapsed;

#endif
    }

    private void GameServiceOnInitialized(object? sender, EventArgs e)
    {
        App.MainWindow.DispatcherQueue.TryEnqueue(() =>
        {
            var categories = ViewModel.GameService.GetCategories();
            var index = 0;
            foreach (var category in categories)
            {
                var categoryViewItem = new NavigationViewItem()
                {
                    Content = category.DisplayNamePlural,
                    Tag = category.InternalName.Id
                };
                NavigationHelper.SetNavigateToParameter(categoryViewItem, category);
                NavigationHelper.SetNavigateTo(categoryViewItem, typeof(CharactersViewModel).FullName!);


                switch (category.ModCategory)
                {
                    case ModCategory.Character:
                        categoryViewItem.Icon = new FontIcon() { Glyph = "\uE716" };

                        ViewModel.NavigationViewService.MenuItems!.Insert(index, categoryViewItem);
                        break;
                    case ModCategory.NPC:
                        categoryViewItem.Icon = new BitmapIcon()
                        {
                            UriSource = new Uri($"{App.ASSET_DIR}/NPC_Icon.png"),
                            ShowAsMonochrome = true
                        };

                        ViewModel.NavigationViewService.MenuItems!.Insert(index, categoryViewItem);
                        break;
                    case ModCategory.Object:
                        categoryViewItem.Icon = new FontIcon() { Glyph = "\uE8FC" };

                        ViewModel.NavigationViewService.MenuItems!.Insert(index, categoryViewItem);
                        break;
                    case ModCategory.Weapons:
                        categoryViewItem.Icon = new BitmapIcon()
                        {
                            UriSource = new Uri($"{App.ASSET_DIR}/Weapon_Icon.png"),
                            ShowAsMonochrome = false
                        };

                        ViewModel.NavigationViewService.MenuItems!.Insert(index, categoryViewItem);
                        break;


                    case ModCategory.Ui:
                    case ModCategory.Gliders:
                    case ModCategory.Custom:
                    default:
                        categoryViewItem.Icon = new FontIcon() { Glyph = "\uF142" };
                        ViewModel.NavigationViewService.MenuItems!.Insert(index, categoryViewItem);
                        break;
                }

                index++;

                //const string menuName = "Categories";
                //if (NavigationViewControl.MenuItems[1] is NavigationViewItem { Tag: not null } menuItem &&
                //    menuItem.Tag.Equals(menuName))
                //{
                //    menuItem.MenuItems.Add(categoryViewItem);
                //}
                //else
                //{
                //    var categoriesItem = new NavigationViewItem()
                //    {
                //        Content = menuName,
                //        Icon = new FontIcon() { Glyph = "\uE712" },
                //        Tag = menuName,
                //        SelectsOnInvoked = false
                //    };


                //    categoriesItem.MenuItems.Add(categoryViewItem);

                //    NavigationHelper.SetNavigateToParameter(categoryViewItem, category);
                //    NavigationViewControl.MenuItems.Insert(1, categoriesItem);
                //    categoriesItem.IsExpanded = true;
                //}
            }
        });

        App.MainWindow.DispatcherQueue.EnqueueAsync(async () =>
        {
            var notSelectedGame = await ViewModel.SelectedGameService.GetNotSelectedGameAsync();

            foreach (var game in notSelectedGame.Reverse())
            {
                var content = new StackPanel()
                {
                    Orientation = Orientation.Horizontal
                };

                var gameInfo = await GameService.GetGameInfoAsync(game);

                if (gameInfo is null)
                    return;

                content.Children.Add(new Grid()
                {
                    Width = 20,
                    Height = 20,
                    CornerRadius = new CornerRadius(8),
                    Children =
                {
                    new Image()
                    {
                        Source = new BitmapImage(new Uri(gameInfo.GameIcon)) { DecodePixelWidth = 20 }
                    }
                }
                });

                content.Children.Add(new TextBlock()
                {
                    Text = gameInfo.GameName,
                    Margin = new Thickness(16, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                });

                var navigationItem = new NavigationViewItem()
                {
                    Name = "SwitchGameButton",
                    Content = content,
                    Tag = $"{gameInfo.GameShortName}"
                };

                NavigationViewControl.FooterMenuItems.Insert(0, navigationItem);
                navigationItem.DoubleTapped += SwitchGameButtonOnDoubleTapped;

                var toolTip = new ToolTip
                {
                    Content = $"Double click to switch to {gameInfo.GameName}"
                };

                ToolTipService.SetToolTip(navigationItem, toolTip);
            }
        });
    }

    private void GlobalMouseHandler_Invoked(object sender, PointerRoutedEventArgs e)
    {
        if (!IsEnabled)
            return;

        // Check if mouse 4 or 5 is clicked
        var mouseButton = e.GetCurrentPoint(this).Properties.PointerUpdateKind;

        if (mouseButton is not (PointerUpdateKind.XButton1Pressed or PointerUpdateKind.XButton2Pressed)) return;

        var navigationService = App.GetService<INavigationService>();

        switch (mouseButton)
        {
            case PointerUpdateKind.XButton1Pressed when navigationService.CanGoBack:
                navigationService.GoBack();
                break;
            case PointerUpdateKind.XButton2Pressed when navigationService.CanGoForward:
                navigationService.GoForward();
                break;
        }
    }


    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        TitleBarHelper.UpdateTitleBar(RequestedTheme);

        KeyboardAccelerators.Add(BuildKeyboardAccelerator(VirtualKey.Left, VirtualKeyModifiers.Menu));
        KeyboardAccelerators.Add(BuildKeyboardAccelerator(VirtualKey.GoBack));
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        var resource = args.WindowActivationState == WindowActivationState.Deactivated
            ? "WindowCaptionForegroundDisabled"
            : "WindowCaptionForeground";

        AppTitleBarText.Foreground = (SolidColorBrush)Application.Current.Resources[resource];
        App.AppTitlebar = AppTitleBarText as UIElement;
    }

    /// <summary>
    /// 提权时亮出那条常驻提示（文案与解除办法在 XAML 与 zh-cn 资源里）。
    ///
    /// <para>
    /// 判据用 <see cref="AppElevation.IsDragDropBlocked"/> 而不是「是不是管理员」：只有「我们比 shell
    /// 的完整性级别高」才是拖拽真被掐掉的充要条件 —— 关掉 UAC 的机器上 explorer 自己也是高完整性、
    /// 两者同级，拖拽本来是好的，拿「是不是管理员」去判就会亮一条假警报，把能用的用户也赶去折腾权限。
    /// </para>
    /// </summary>
    private void ShowElevationNoticeIfDragDropIsBlocked()
    {
        if (!AppElevation.IsDragDropBlocked())
            return;

        ElevationNotice.Title = LocalizeOrFallback("ShellPage_ElevationNoticeTitle",
            "JASM 正以管理员身份运行 —— 拖拽安装不可用");

        ElevationNotice.Message = LocalizeOrFallback("ShellPage_ElevationNoticeMessage",
            "拖 Mod 进 JASM 只会显示禁止光标、松手没反应：Windows 不允许把文件从中等权限的程序拖进高权限的程序。"
            + "点右边按钮切回普通权限；也可以自己右键 JASM 的快捷方式 → 属性 → 兼容性 → 取消勾选「以管理员身份运行此程序」。");

        ElevationNotice.IsOpen = true;
    }

    /// <summary>
    /// 取本地化文案；取不到就用内联默认。
    ///
    /// <para>
    /// <b>为什么必须有兜底</b>：<see cref="ResourceExtensions.GetLocalized"/> 底下是
    /// <c>ResourceLoader.GetString</c>，key 缺失时它**会抛**。而这里是在页面的 <c>Loaded</c> 里调的 ——
    /// 异常会把整个页面初始化打断，表现就是「启动就崩」，而真正的原因只是某条文案没进 PRI。
    /// 文案缺失是配置问题，不该升级成崩溃；也不该让用户对着一条**内容为空、看着像不存在**的提示条猜
    /// （那正是这次排查里被误导过的地方：空提示条与「没触发」在肉眼上分不出来）。
    /// </para>
    /// </summary>
    private static string LocalizeOrFallback(string key, string fallback)
    {
        try
        {
            var text = ResourceExtensions.GetLocalized(key);
            return string.IsNullOrWhiteSpace(text) ? fallback : text;
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    private async void ElevationNoticeRestart_Click(object sender, RoutedEventArgs e)
    {
        // 失败时 RestartWithoutElevationAsync 自己会弹通知 —— 绝不静默失败
        await App.GetService<LifeCycleService>().RestartWithoutElevationAsync();
    }

    private void NavigationViewControl_DisplayModeChanged(NavigationView sender,
        NavigationViewDisplayModeChangedEventArgs args)
    {
        AppTitleBar.Margin = new Thickness()
        {
            Left = ViewModel.IsNotFirstTimeStartupPage
                ? sender.CompactPaneLength * (sender.DisplayMode == NavigationViewDisplayMode.Minimal ? 2 : 1)
                : 32,
            Top = AppTitleBar.Margin.Top,
            Right = AppTitleBar.Margin.Right,
            Bottom = AppTitleBar.Margin.Bottom
        };
    }

    private static KeyboardAccelerator BuildKeyboardAccelerator(VirtualKey key, VirtualKeyModifiers? modifiers = null)
    {
        var keyboardAccelerator = new KeyboardAccelerator() { Key = key };

        if (modifiers.HasValue) keyboardAccelerator.Modifiers = modifiers.Value;

        keyboardAccelerator.Invoked += OnKeyboardAcceleratorInvoked;

        return keyboardAccelerator;
    }

    private static void OnKeyboardAcceleratorInvoked(KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        var navigationService = App.GetService<INavigationService>();

        var result = navigationService.GoBack();

        args.Handled = result;
    }

    private readonly VirtualKey[] _code = new[]
    {
        VirtualKey.Up, VirtualKey.Up, VirtualKey.Down, VirtualKey.Down, VirtualKey.Left, VirtualKey.Right,
        VirtualKey.Left, VirtualKey.Right, VirtualKey.B, VirtualKey.A, VirtualKey.Enter, VirtualKey.Space
    };

    private readonly List<VirtualKey> _codeKeys = new();

    private async void GlobalKeyHandler_Invoked(object sender, KeyRoutedEventArgs e)
    {
        if (!IsEnabled)
            return;

        if (e.Key == VirtualKey.F10)
        {
            await ViewModel.RefreshGenshinMods();
            return;
        }


        if (_code.Contains(e.Key))
        {
            //_codeKeys.AddRange(_code[..^2].Append(VirtualKey.Space));
            _codeKeys.Add(e.Key);
        }
        else
            _codeKeys.Clear();


        if (_codeKeys.Count == _code.Length - 1 &&
            _codeKeys.SequenceEqual(_code.Take(_code.Length - 2).Append(VirtualKey.Space)) ||
            _codeKeys.SequenceEqual(_code.Take(_code.Length - 2).Append(VirtualKey.Enter)))
        {
            Perform_XD();
            _codeKeys.Clear();
        }
        else if (_code.Length < _codeKeys.Count - 1)
            _codeKeys.Clear();
    }

    private void Perform_XD()
    {
        App.GetService<INavigationService>().NavigateTo(typeof(EasterEggVM).FullName!);
    }

    private async void SwitchGameButtonOnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is not NavigationViewItem)
            return;
        e.Handled = true;
        await Task.Delay(200);

        if (sender is FrameworkElement { Tag: string gameName })
        {
            await App.GetService<LifeCycleService>()
                .RestartAsync(notifyOnError: true,
                    postShutdownLogic: () => ViewModel.SelectedGameService.SetSelectedGame(gameName))
                    .ConfigureAwait(false);
        }
    }
}