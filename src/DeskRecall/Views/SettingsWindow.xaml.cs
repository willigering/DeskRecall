using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using DeskRecall.Services;
using DeskRecall.ViewModels;

namespace DeskRecall.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;

    public SettingsWindow(SettingsViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        SourceInitialized += (_, _) => UiNative.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
        Closing += (_, _) => { _vm.Flush(); _vm.Rules.Flush(); };
    }

    public void ShowPage(string key)
    {
        var page = _vm.Pages.FirstOrDefault(p => p.Key == key);
        if (page is not null) _vm.SelectedPage = page;
    }

    private void RunningAppsCombo_DropDownOpened(object? sender, EventArgs e)
    {
        if (sender is ComboBox cb) cb.GetBindingExpression(ItemsControl.ItemsSourceProperty)?.UpdateTarget();
    }

    private void RunningAppsCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox cb || cb.SelectedItem is null) return;
        _vm.Rules.AddFromRunningCommand.Execute(cb.SelectedItem);
        cb.SelectedItem = null;
    }
}
