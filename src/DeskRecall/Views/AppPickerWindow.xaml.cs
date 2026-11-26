using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DeskRecall.Core.Apps;
using DeskRecall.Services;
using DeskRecall.ViewModels;

namespace DeskRecall.Views;

public sealed class AppPickItem
{
    public required InstalledAppEntry Entry { get; init; }
    public required string Name { get; init; }
    public required string Subtitle { get; init; }
    public ImageSource? Icon { get; set; }
}

public partial class AppPickerWindow : Window
{
    readonly IInstalledAppSource _source;
    readonly IDialogService _dialogs;
    readonly IconService _icons;
    readonly List<InstalledAppEntry> _all = new();
    readonly ObservableCollection<AppPickItem> _visible = new();

    public InstalledAppEntry? Selected { get; private set; }
    /// <summary>„Hinzufügen und starten“ gewählt.</summary>
    public bool LaunchAfterAdd { get; private set; }

    public AppPickerWindow(IInstalledAppSource source, IDialogService dialogs, IconService icons, string desktopName)
    {
        InitializeComponent();
        _source = source;
        _dialogs = dialogs;
        _icons = icons;
        TitleText.Text = string.IsNullOrWhiteSpace(desktopName)
            ? "Programm hinzufügen"
            : $"Programm zu „{desktopName}“ hinzufügen";
        AppList.ItemsSource = _visible;
        SourceInitialized += (_, _) =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            UiNative.UseDarkTitleBar(hwnd);
            UiNative.RoundCorners(hwnd);
        };
        Loaded += async (_, _) =>
        {
            SearchBox.Focus();
            await LoadAsync();
        };
    }

    async Task LoadAsync()
    {
        try
        {
            var list = await _source.ListAsync();
            _all.Clear();
            _all.AddRange(list);
            ApplyFilter();
        }
        catch
        {
            EmptyText.Text = "Die Programmliste konnte nicht gelesen werden. Sie können eine Datei über „Durchsuchen…“ wählen.";
        }
    }

    void ApplyFilter()
    {
        var q = SearchBox.Text;
        var hits = InstalledAppCatalog.Search(_all, q);
        _visible.Clear();
        foreach (var e in hits.Take(400))
        {
            var sub = e.IsUwp
                ? (e.AppUserModelId ?? "Store-App")
                : (e.TargetPath ?? e.ShortcutPath ?? "");
            _visible.Add(new AppPickItem
            {
                Entry = e,
                Name = e.Name,
                Subtitle = sub,
                Icon = _icons.ForPath(e.IconPath ?? e.TargetPath ?? e.ShortcutPath),
            });
        }
        EmptyText.Visibility = _visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _all.Count == 0
            ? "Keine Programme gefunden. Nutzen Sie „Durchsuchen…“ oder ziehen Sie eine Datei auf eine Desktop-Karte."
            : "Keine Treffer.";
        AppList.Visibility = _visible.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = string.IsNullOrWhiteSpace(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        if (_all.Count > 0) ApplyFilter();
    }

    void AppList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => OkButton.IsEnabled = OkLaunchButton.IsEnabled = AppList.SelectedItem is AppPickItem;

    void AppList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (AppList.SelectedItem is AppPickItem) Accept();
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var path = _dialogs.OpenFile("Programm wählen", "Programme und Verknüpfungen|*.exe;*.lnk|Programme (*.exe)|*.exe|Verknüpfungen (*.lnk)|*.lnk");
        if (string.IsNullOrWhiteSpace(path)) return;
        var entry = _source.ResolveShortcut(path) ?? InstalledAppCatalog.FromDroppedPath(path);
        if (entry is null)
        {
            _dialogs.Info("Datei nicht nutzbar", "Bitte eine ausführbare Datei (.exe) oder eine Verknüpfung (.lnk) wählen.");
            return;
        }
        Selected = entry;
        DialogResult = true;
        Close();
    }

    void Ok_Click(object sender, RoutedEventArgs e) => Accept();
    void OkLaunch_Click(object sender, RoutedEventArgs e) { LaunchAfterAdd = true; Accept(); }
    void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }

    void Accept()
    {
        if (AppList.SelectedItem is not AppPickItem item) return;
        Selected = item.Entry;
        DialogResult = true;
        Close();
    }

    void Title_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }

    public static (InstalledAppEntry Entry, bool Launch)? Pick(Window? owner, IInstalledAppSource source, IDialogService dialogs, IconService icons, string desktopName)
    {
        var w = new AppPickerWindow(source, dialogs, icons, desktopName);
        if (owner is not null) w.Owner = owner;
        return w.ShowDialog() == true && w.Selected is { } e ? (e, w.LaunchAfterAdd) : null;
    }
}
