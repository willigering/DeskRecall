using System.Collections.ObjectModel;
using System.Windows.Media;
using DeskRecall.Core.Browsers;
using DeskRecall.Core.Models;

namespace DeskRecall.ViewModels;

/// <summary>Unterzeile eines Programms: ein Fenster, bei Browsern mit seinen Tabs.</summary>
public sealed class WindowItemViewModel : ObservableObject
{
    private WindowRecord _record;
    private bool _showHeader, _isTabsExpanded;

    public WindowItemViewModel(WindowRecord record) { _record = record; }

    private ImageSource? _icon;
    /// <summary>Programmsymbol (z. B. Firefox) für die Tab-Zeilen.</summary>
    public ImageSource? Icon { get => _icon; set => Set(ref _icon, value); }

    public WindowRecord Record => _record;
    public long Handle => _record.Handle;
    public Guid DesktopId => _record.DesktopId;
    public string DisplayTitle => _record.DisplayTitle;
    public WindowShowState State => _record.State;
    public BrowserFamily Family { get; set; }
    public ObservableCollection<TabRowViewModel> Tabs { get; } = new();
    public bool HasTabs => Tabs.Count > 1;
    public string TabCountText => Tabs.Count.ToString();

    /// <summary>Fensterzeile anzeigen (nur bei mehreren Fenstern; bei einem Fenster stehen die Tabs direkt unter dem Programm).</summary>
    public bool ShowHeader { get => _showHeader; set { if (Set(ref _showHeader, value)) OnPropertyChanged(nameof(TabsOpen)); } }
    public bool IsTabsExpanded { get => _isTabsExpanded; set { if (Set(ref _isTabsExpanded, value && HasTabs)) OnPropertyChanged(nameof(TabsOpen)); } }
    public bool TabsOpen => HasTabs && (!ShowHeader || IsTabsExpanded);

    public void Update(WindowRecord record)
    {
        var old = _record;
        _record = record;
        if (old.Title != record.Title) OnPropertyChanged(nameof(DisplayTitle));
        if (old.State != record.State) OnPropertyChanged(nameof(State));
    }

    /// <summary>Tabs per Index abgleichen; laufende Umbenennungen bleiben erhalten.</summary>
    public bool SyncTabs(IReadOnlyList<BrowserTab> tabs, string processName, Func<string, string?> aliasFor)
    {
        var changed = false;
        for (var i = 0; i < tabs.Count; i++)
        {
            var t = tabs[i];
            if (i < Tabs.Count)
            {
                if (Tabs[i].Update(t, aliasFor(t.Title))) changed = true;
            }
            else
            {
                Tabs.Add(new TabRowViewModel(this, processName, t, aliasFor(t.Title)));
                changed = true;
            }
        }
        while (Tabs.Count > tabs.Count) { Tabs.RemoveAt(Tabs.Count - 1); changed = true; }
        if (changed)
        {
            OnPropertyChanged(nameof(HasTabs));
            OnPropertyChanged(nameof(TabCountText));
            OnPropertyChanged(nameof(TabsOpen));
            if (!HasTabs) IsTabsExpanded = false;
        }
        return changed;
    }
}

/// <summary>Ein Browser-Tab mit optionalem lokalem Namen.</summary>
public sealed class TabRowViewModel : ObservableObject
{
    private BrowserTab _tab;
    private string? _alias;
    private bool _isEditing;
    private string _editName = "";

    public TabRowViewModel(WindowItemViewModel window, string processName, BrowserTab tab, string? alias)
    {
        Window = window; ProcessName = processName; _tab = tab; _alias = alias;
    }

    public WindowItemViewModel Window { get; }
    public string ProcessName { get; }
    public BrowserTab Tab => _tab;
    public string OriginalTitle => TabTitle.Display(_tab.Title);
    public string? Alias => _alias;
    public bool HasAlias => !string.IsNullOrWhiteSpace(_alias);
    public string DisplayTitle => HasAlias ? _alias! : OriginalTitle;
    public string ToolTipText => HasAlias ? $"{_alias}\nOriginal: {OriginalTitle}" : OriginalTitle;
    public bool IsSelected => _tab.IsSelected;
    public bool IsEditing { get => _isEditing; set => Set(ref _isEditing, value); }
    public string EditName { get => _editName; set => Set(ref _editName, value); }

    public bool Update(BrowserTab tab, string? alias)
    {
        var changed = false;
        if (_tab != tab)
        {
            var titleChanged = _tab.Title != tab.Title;
            var selChanged = _tab.IsSelected != tab.IsSelected;
            _tab = tab;
            if (titleChanged) { OnPropertyChanged(nameof(OriginalTitle)); OnPropertyChanged(nameof(DisplayTitle)); OnPropertyChanged(nameof(ToolTipText)); }
            if (selChanged) OnPropertyChanged(nameof(IsSelected));
            changed = true;
        }
        if (SetAlias(alias)) changed = true;
        return changed;
    }

    public bool SetAlias(string? alias)
    {
        alias = string.IsNullOrWhiteSpace(alias) ? null : alias;
        if (_alias == alias) return false;
        _alias = alias;
        OnPropertyChanged(nameof(Alias));
        OnPropertyChanged(nameof(HasAlias));
        OnPropertyChanged(nameof(DisplayTitle));
        OnPropertyChanged(nameof(ToolTipText));
        return true;
    }
}
