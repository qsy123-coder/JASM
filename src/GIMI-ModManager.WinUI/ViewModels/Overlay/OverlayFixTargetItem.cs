using CommunityToolkit.Mvvm.ComponentModel;
using GIMI_ModManager.Core.Entities;
using GIMI_ModManager.Core.Entities.Mods.Helpers;

namespace GIMI_ModManager.WinUI.ViewModels.Overlay;

/// <summary>
/// 修复页里一个可勾选的 Mod。路径在进入修复页/换角色时快照，
/// 不在运行中重新解析 —— 修复任务必须拿到开始那一刻的路径。
/// </summary>
internal sealed partial class OverlayFixTargetItem : ObservableObject
{
    public OverlayFixTargetItem(Guid id, string name, string modPath, bool isEnabled)
    {
        Id = id;
        Name = name;
        ModPath = modPath;
        IsEnabled = isEnabled;
    }

    public Guid Id { get; }
    public string Name { get; }
    public string ModPath { get; }
    public bool IsEnabled { get; }

    [ObservableProperty] private bool _isSelected = true;

    /// <summary>「已禁用」小标；勾选状态与启用状态无关（禁用 Mod 也可能需要修）。</summary>
    public bool ShowDisabledHint => !IsEnabled;

    public static OverlayFixTargetItem FromEntry(CharacterSkinEntry entry) =>
        new(entry.Id, entry.Mod.GetDisplayName(), entry.Mod.FullPath, entry.IsEnabled);
}