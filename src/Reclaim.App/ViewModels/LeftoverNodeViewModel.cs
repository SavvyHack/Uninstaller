using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Reclaim.Core.Infrastructure;
using Reclaim.Core.Leftovers;

namespace Reclaim.App.ViewModels;

/// <summary>Tree node in the leftover review: a confidence group, a category, or a single leftover. Checkboxes cascade.</summary>
public sealed partial class LeftoverNodeViewModel : ObservableObject
{
    private bool? _isChecked = false;

    public LeftoverNodeViewModel? Parent { get; private set; }
    public ObservableCollection<LeftoverNodeViewModel> Children { get; } = [];
    public LeftoverItem? Item { get; }
    public string Title { get; }
    public string? Tone { get; init; }
    public bool IsGroup => Item is null;

    [ObservableProperty] private bool _isExpanded;

    public LeftoverNodeViewModel(string title, LeftoverItem? item = null)
    {
        Title = title;
        Item = item;
        if (item is not null) _isChecked = item.Selected;
    }

    public void Add(LeftoverNodeViewModel child)
    {
        child.Parent = this;
        Children.Add(child);
    }

    public bool? IsChecked
    {
        get => _isChecked;
        set => SetChecked(value ?? false, updateChildren: true, updateParent: true);
    }

    private void SetChecked(bool? value, bool updateChildren, bool updateParent)
    {
        if (_isChecked == value) return;
        _isChecked = value;
        if (Item is not null && value.HasValue) Item.Selected = value.Value;
        if (updateChildren && value.HasValue)
            foreach (var c in Children) c.SetChecked(value, true, false);
        if (updateParent) Parent?.Reevaluate();
        OnPropertyChanged(nameof(IsChecked));
        RootChanged?.Invoke();
    }

    private void Reevaluate()
    {
        bool? state = Children.All(c => c.IsChecked == true) ? true : Children.All(c => c.IsChecked == false) ? false : null;
        SetChecked(state, false, true);
    }

    /// <summary>Sets group checkboxes from their leaves without touching the leaves.</summary>
    public void SyncFromChildren()
    {
        if (Item is not null) return;
        foreach (var c in Children) c.SyncFromChildren();
        _isChecked = Children.All(c => c.IsChecked == true) ? true : Children.All(c => c.IsChecked == false) ? false : null;
        OnPropertyChanged(nameof(IsChecked));
    }

    /// <summary>Raised on the root when any checkbox in the tree changes, so totals update.</summary>
    public event Action? RootChanged;

    public void BubbleTo(Action handler)
    {
        RootChanged += handler;
        foreach (var c in Children) c.BubbleTo(handler);
    }

    public IEnumerable<LeftoverItem> Items => Item is not null ? [Item] : Children.SelectMany(c => c.Items);

    public string Detail => Item is null
        ? $"{Items.Count()} item{(Items.Count() == 1 ? "" : "s")}{(Items.Sum(i => i.SizeBytes) is > 0 and var s ? " · " + Format.Bytes(s) : "")}"
        : Item.SizeText;

    public string KindGlyph => Item?.Kind switch
    {
        LeftoverKind.Directory => "Folder24",
        LeftoverKind.File => "Document24",
        LeftoverKind.RegistryKey => "Database24",
        LeftoverKind.RegistryValue => "TextBulletListSquare24",
        LeftoverKind.Service => "Settings24",
        LeftoverKind.ScheduledTask => "CalendarClock24",
        LeftoverKind.FirewallRule => "ShieldKeyhole24",
        LeftoverKind.EnvironmentPath => "Code24",
        _ => "Folder24",
    };

    public string? ToolTipText => Item is null ? null : $"{Item.Confidence}% confidence\n{Item.Reason}";
}
