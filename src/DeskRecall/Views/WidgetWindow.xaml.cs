using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DeskRecall.Core.Models;
using DeskRecall.Core.Settings;
using DeskRecall.Services;
using DeskRecall.ViewModels;
using static DeskRecall.Services.UiNative;

namespace DeskRecall.Views;

/// <summary>
/// Das Widget am Bildschirmrand. Es nimmt niemals den Fokus (WS_EX_NOACTIVATE + Toolfenster),
/// erscheint nicht in Alt-Tab/Taskleiste und blendet sich bei Vollbild-Anwendungen aus.
/// </summary>
public partial class WidgetWindow : Window
{
    private enum Presentation { Expanded, Strip, Edge }

    private readonly WidgetViewModel _vm;
    private readonly AppController _controller;
    private readonly ISettingsService _settings;
    private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private IntPtr _hwnd;
    private Presentation _presentation = Presentation.Expanded;
    private bool _userCollapsed;
    private bool _hiddenForFullscreen;
    private bool _keyboardMode;
    private bool _acrylicActive;
    private Point _dragStart;
    private AppRowViewModel? _dragRow;

    public event Action? SettingsRequested;

    public WidgetWindow(WidgetViewModel vm, AppController controller, ISettingsService settings)
    {
        InitializeComponent();
        _vm = vm; _controller = controller; _settings = settings;
        DataContext = vm;
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); if (!IsMouseOver && !IsAnyPopupOpen()) ApplyPresentation(Presentation.Edge); };
        _settings.Changed += _ => Dispatcher.BeginInvoke(ApplySettings);
        _controller.FullscreenChanged += OnFullscreenChanged;
        SourceInitialized += OnSourceInitialized;
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WidgetViewModel.StatusOk)) UpdateStatusDot();
            if (e.PropertyName == nameof(WidgetViewModel.IsStatusOpen)) StatusChevron.Text = _vm.IsStatusOpen ? "\uE70E" : "\uE70D";
        };
        IsVisibleChanged += (_, _) => { _controller.UiActive = IsVisible && !_hiddenForFullscreen; if (IsVisible) _controller.MarkDirty(); };
    }

    public bool IsWidgetVisible => IsVisible;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        _controller.WidgetHandle = _hwnd;
        SetNoActivate(true);
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        ApplySettings();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Klicks aktivieren das Widget nicht – das Spiel/Programm im Vordergrund behält den Fokus.
        if (msg == WM_MOUSEACTIVATE && !_keyboardMode) { handled = true; return (IntPtr)MA_NOACTIVATE; }
        if (msg is WM_DISPLAYCHANGE or WM_DPICHANGED || (msg == WM_SETTINGCHANGE && (int)wParam == 0x2F)) Dispatcher.BeginInvoke(PositionWindow, DispatcherPriority.Background);
        return IntPtr.Zero;
    }

    private void SetNoActivate(bool on)
    {
        if (_hwnd == IntPtr.Zero) return;
        var ex = (long)GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
        ex |= WS_EX_TOOLWINDOW;
        ex &= ~WS_EX_APPWINDOW;
        ex = on ? ex | WS_EX_NOACTIVATE : ex & ~WS_EX_NOACTIVATE;
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, (IntPtr)ex);
    }

    // ------------------------------------------------------------ Einstellungen / Darstellung

    public void ApplySettings()
    {
        var w = _settings.Current.Widget;
        Topmost = w.AlwaysOnTop;
        var scale = Math.Clamp(w.ScalePercent, 75, 150) / 100.0;
        RootScale.ScaleX = RootScale.ScaleY = scale;
        ExpandGlyph.Text = w.Dock == WidgetDock.Right ? "\uE76B" : "\uE76C";
        CollapseButton.Visibility = w.Mode == WidgetDisplayMode.AlwaysExpanded ? Visibility.Collapsed : Visibility.Visible;
        CollapseButton.ToolTip = w.Mode == WidgetDisplayMode.TrayOnly ? "Ausblenden (Infobereich)" : "Einklappen";
        ApplyBackground();
        var target = w.Mode switch
        {
            WidgetDisplayMode.AlwaysExpanded => Presentation.Expanded,
            WidgetDisplayMode.AutoHide => IsMouseOver ? Presentation.Expanded : Presentation.Edge,
            WidgetDisplayMode.Collapsible => _userCollapsed ? Presentation.Strip : Presentation.Expanded,
            _ => Presentation.Expanded,
        };
        ApplyPresentation(target);
    }

    private void ApplyBackground()
    {
        var w = _settings.Current.Widget;
        var alpha = (byte)Math.Round(Math.Clamp(w.OpacityPercent, 30, 100) * 2.55);
        _acrylicActive = false;
        if (w.Acrylic && _hwnd != IntPtr.Zero && _presentation != Presentation.Edge)
        {
            // Tönung im Format AABBGGRR – Anthrazit #1E1B26
            var tint = ((uint)Math.Max(0x50, alpha - 0x30) << 24) | (0x26u << 16) | (0x1Bu << 8) | 0x1Eu;
            _acrylicActive = EnableAcrylic(_hwnd, tint);
        }
        else if (_hwnd != IntPtr.Zero) DisableAcrylic(_hwnd);

        // Mit Acryl darf die WPF-Fläche transparenter sein, ohne Acryl trägt sie die Deckkraft allein.
        var panelAlpha = _acrylicActive ? (byte)Math.Max(0x40, alpha - 0x50) : alpha;
        var brush = new SolidColorBrush(Color.FromArgb(panelAlpha, 0x1E, 0x1B, 0x26));
        brush.Freeze();
        ExpandedPanel.Background = brush;
        StripPanel.Background = brush;
        UpdateRegion();
    }

    private void ApplyPresentation(Presentation p)
    {
        _presentation = p;
        ExpandedPanel.Visibility = p == Presentation.Expanded ? Visibility.Visible : Visibility.Collapsed;
        StripPanel.Visibility = p == Presentation.Strip ? Visibility.Visible : Visibility.Collapsed;
        EdgeTrigger.Visibility = p == Presentation.Edge ? Visibility.Visible : Visibility.Collapsed;
        _vm.IsExpanded = p == Presentation.Expanded;
        if (p == Presentation.Edge && _hwnd != IntPtr.Zero) DisableAcrylic(_hwnd);
        else if (_hwnd != IntPtr.Zero && _settings.Current.Widget.Acrylic && !_acrylicActive) ApplyBackground();
        PositionWindow();
    }

    /// <summary>Positioniert das Widget am rechten/linken Rand des Arbeitsbereichs (Taskleiste bleibt frei).</summary>
    public void PositionWindow()
    {
        var w = _settings.Current.Widget;
        var work = GetWorkArea();
        var scale = RootScale.ScaleX;
        const double margin = 8;
        double width = _presentation switch
        {
            Presentation.Expanded => Math.Clamp(w.ExpandedWidth, 260, 420) * scale,
            Presentation.Strip => 44 * scale,
            _ => 3,
        };
        double height = _presentation == Presentation.Expanded || _presentation == Presentation.Edge
            ? work.Height - 2 * margin
            : Math.Min(work.Height - 2 * margin, MeasureStripHeight());
        var left = w.Dock == WidgetDock.Right ? work.Right - width - (_presentation == Presentation.Edge ? 0 : margin) : work.Left + (_presentation == Presentation.Edge ? 0 : margin);
        var top = _presentation == Presentation.Strip ? work.Top + (work.Height - height) / 2 : work.Top + margin;
        Width = width; Height = height; Left = left; Top = top;
        UpdateRegion();
    }

    private double MeasureStripHeight()
    {
        StripPanel.Measure(new Size(44, double.PositiveInfinity));
        return Math.Max(160, StripPanel.DesiredSize.Height * RootScale.ScaleY + 4);
    }

    /// <summary>Arbeitsbereich des Monitors, auf dem das Widget liegt (bzw. Hauptmonitor) – in WPF-Einheiten.</summary>
    private Rect GetWorkArea()
    {
        try
        {
            IntPtr mon = _hwnd != IntPtr.Zero ? MonitorFromWindow(_hwnd, 1 /* PRIMARY */) : MonitorFromPoint(new POINT(), 1);
            if (_firstPlacement) { mon = MonitorFromPoint(new POINT(), 1); _firstPlacement = false; }
            var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(mon, ref mi))
            {
                GetDpiForMonitor(mon, 0, out var dx, out var dy);
                var sx = dx == 0 ? 1 : dx / 96.0; var sy = dy == 0 ? 1 : dy / 96.0;
                return new Rect(mi.rcWork.Left / sx, mi.rcWork.Top / sy, (mi.rcWork.Right - mi.rcWork.Left) / sx, (mi.rcWork.Bottom - mi.rcWork.Top) / sy);
            }
        }
        catch { }
        return SystemParameters.WorkArea;
    }
    private bool _firstPlacement = true;

    /// <summary>Abgerundete Fensterregion, damit die Acryl-Unschärfe nicht über die Ecken hinausragt.</summary>
    private void UpdateRegion()
    {
        if (_hwnd == IntPtr.Zero) return;
        try
        {
            var dpi = GetDpiForWindow(_hwnd) / 96.0;
            if (dpi <= 0) dpi = 1;
            var w = (int)Math.Ceiling(Width * dpi); var h = (int)Math.Ceiling(Height * dpi);
            var r = (int)Math.Round(24 * dpi * RootScale.ScaleX);
            var rgn = _acrylicActive && _presentation != Presentation.Edge ? CreateRoundRectRgn(0, 0, w + 1, h + 1, r, r) : IntPtr.Zero;
            SetWindowRgn(_hwnd, rgn, true); // Region gehört danach dem System
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool redraw);

    private void UpdateStatusDot()
    {
        var b = (Brush)FindResource(_vm.StatusOk ? "GreenBrush" : "DangerBrush");
        StatusDot.Fill = b; StripDot.Fill = b;
    }

    // ------------------------------------------------------------ Vollbild

    private void OnFullscreenChanged(bool fullscreen)
    {
        if (_settings.Current.Widget.Fullscreen != FullscreenBehavior.AutoHide) return;
        if (fullscreen && IsVisible) { _hiddenForFullscreen = true; Hide(); }
        else if (!fullscreen && _hiddenForFullscreen) { _hiddenForFullscreen = false; if (_settings.Current.Widget.Mode != WidgetDisplayMode.TrayOnly || _trayShown) ShowWidget(); }
        _controller.UiActive = IsVisible && !_hiddenForFullscreen;
    }

    private bool _trayShown;

    /// <summary>Zeigt das Widget ohne es zu aktivieren.</summary>
    public void ShowWidget()
    {
        if (_controller.IsFullscreen && _settings.Current.Widget.Fullscreen == FullscreenBehavior.AutoHide) { _hiddenForFullscreen = true; return; }
        if (!IsVisible) Show();
        PositionWindow();
        _controller.UiActive = true;
    }

    /// <summary>Klick auf das Tray-Symbol.</summary>
    public void ToggleFromTray()
    {
        var mode = _settings.Current.Widget.Mode;
        if (mode == WidgetDisplayMode.TrayOnly)
        {
            if (IsVisible) { _trayShown = false; Hide(); }
            else { _trayShown = true; ShowWidget(); ApplyPresentation(Presentation.Expanded); }
            return;
        }
        if (!IsVisible) { ShowWidget(); return; }
        if (_presentation != Presentation.Expanded) { _userCollapsed = false; ApplyPresentation(Presentation.Expanded); }
        else if (mode == WidgetDisplayMode.Collapsible) { _userCollapsed = true; ApplyPresentation(Presentation.Strip); }
    }

    private bool IsAnyPopupOpen() => _vm.IsHistoryOpen || ProfileCombo.IsDropDownOpen || _menuOpen || _keyboardMode || DragActive;
    private bool _menuOpen;
    private bool DragActive => _dragRow is not null;

    // ------------------------------------------------------------ Kopf / Ein- und Ausklappen

    private void Settings_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();

    private void Collapse_Click(object sender, RoutedEventArgs e)
    {
        var mode = _settings.Current.Widget.Mode;
        if (mode == WidgetDisplayMode.TrayOnly) { _trayShown = false; Hide(); return; }
        if (mode == WidgetDisplayMode.AutoHide) { ApplyPresentation(Presentation.Edge); return; }
        _userCollapsed = true;
        ApplyPresentation(Presentation.Strip);
    }

    private void Expand_Click(object sender, RoutedEventArgs e)
    {
        _userCollapsed = false;
        ApplyPresentation(Presentation.Expanded);
    }

    private void Expand_Click(object sender, MouseButtonEventArgs e) => Expand_Click(sender, (RoutedEventArgs)e);

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && _settings.Current.Widget.Mode == WidgetDisplayMode.Collapsible) { Collapse_Click(sender, e); }
    }

    private void Status_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => _vm.ToggleStatusCommand.Execute(null);

    private void Panel_MouseEnter(object sender, MouseEventArgs e) => _hideTimer.Stop();

    private void Panel_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_settings.Current.Widget.Mode == WidgetDisplayMode.AutoHide && _presentation == Presentation.Expanded) _hideTimer.Start();
    }

    private void Edge_MouseEnter(object sender, MouseEventArgs e)
    {
        if (_settings.Current.Widget.Mode == WidgetDisplayMode.AutoHide) ApplyPresentation(Presentation.Expanded);
    }

    // ------------------------------------------------------------ Karten

    private static T? Ctx<T>(object sender) where T : class => (sender as FrameworkElement)?.DataContext as T;

    private void CardHeader_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (Ctx<DesktopCardViewModel>(sender) is not { } card || card.IsEditing) return;
        if (e.OriginalSource is DependencyObject d && FindParent<Button>(d) is not null) return;
        _vm.SwitchDesktopCommand.Execute(card);
    }

    protected override void OnPreviewMouseDoubleClick(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseDoubleClick(e);
        if (e.OriginalSource is DependencyObject d && FindParent<Grid>(d, g => g.Name == "CardHeader") is { DataContext: DesktopCardViewModel card })
        {
            _vm.BeginRenameCommand.Execute(card);
            e.Handled = true;
        }
    }

    private void StripChip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (Ctx<DesktopCardViewModel>(sender) is { } card) _vm.SwitchDesktopCommand.Execute(card);
    }

    private void Card_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not DesktopCardViewModel card || fe.ContextMenu is not { } menu) return;
        if (e.OriginalSource is DependencyObject d && FindParent<Border>(d, b => b.Name == "Row") is not null) { e.Handled = true; return; } // Zeilen haben eigenes Menü
        menu.Items.Clear();
        menu.Items.Add(Item("Zu diesem Desktop wechseln", "\uE8A7", () => _vm.SwitchDesktopCommand.Execute(card)));
        menu.Items.Add(Item("Umbenennen", "\uE70F", () => _vm.BeginRenameCommand.Execute(card), _vm.CanManageDesktops));
        menu.Items.Add(Item(card.IsExpanded ? "Einklappen" : "Ausklappen", card.IsExpanded ? "\uE70E" : "\uE70D", () => _vm.ToggleCardCommand.Execute(card)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Neuer Desktop", "\uE710", () => _vm.AddDesktopCommand.Execute(null), _vm.CanManageDesktops));
        menu.Items.Add(Item("Desktop schließen …", "\uE74D", () => _vm.DeleteDesktopCommand.Execute(card), _vm.CanManageDesktops && _vm.Cards.Count > 1));
        HookMenu(menu);
    }

    private void Row_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not AppRowViewModel row || fe.ContextMenu is not { } menu) return;
        menu.Items.Clear();
        menu.Items.Add(Item("In den Vordergrund holen", "\uE8A7", () => _vm.ActivateAppCommand.Execute(row)));
        if (row.Windows.Count > 1)
        {
            var wins = new MenuItem { Header = "Fenster", Icon = Glyph("\uE737") };
            foreach (var w in row.Windows)
            {
                var win = w;
                wins.Items.Add(Item(string.IsNullOrWhiteSpace(w.Title) ? "(ohne Titel)" : Trim(w.Title, 48), null, () => _vm.ActivateWindowCommand.Execute(win)));
            }
            menu.Items.Add(wins);
        }
        var move = new MenuItem { Header = "Verschieben nach", Icon = Glyph("\uE8DE"), IsEnabled = _vm.CanManageDesktops };
        foreach (var c in _vm.Cards)
        {
            var target = c;
            move.Items.Add(Item($"{c.Number}  {c.DisplayName}", c.Id == row.DesktopId ? "\uE73E" : null, () => _ = _vm.MoveAppAsync(row, target), c.Id != row.DesktopId));
        }
        move.Items.Add(new Separator());
        move.Items.Add(Item("Neuer Desktop", "\uE710", () => _ = _vm.CreateDesktopAndMoveAsync(row)));
        menu.Items.Add(move);
        menu.Items.Add(Item("Auf allen Desktops anzeigen", "\uE718", () => _vm.PinAppCommand.Execute(row), _vm.CanManageDesktops));
        menu.Items.Add(Item("Minimieren", "\uE921", () => _vm.MinimizeAppCommand.Execute(row)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(row.Windows.Count > 1 ? "Alle Fenster schließen …" : "Schließen …", "\uE711", () => _vm.CloseAppCommand.Execute(row)));
        HookMenu(menu);
    }

    private void HookMenu(ContextMenu menu)
    {
        _menuOpen = true;
        menu.Closed -= Menu_Closed;
        menu.Closed += Menu_Closed;
    }

    private void Menu_Closed(object sender, RoutedEventArgs e) => _menuOpen = false;

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    private static TextBlock Glyph(string g) => new() { Text = g, FontFamily = (FontFamily)Application.Current.FindResource("IconFont"), FontSize = 12 };

    private static MenuItem Item(string header, string? glyph, Action action, bool enabled = true)
    {
        var mi = new MenuItem { Header = header, IsEnabled = enabled };
        if (glyph is not null) mi.Icon = Glyph(glyph);
        mi.Click += (_, _) => action();
        return mi;
    }

    // ------------------------------------------------------------ Zeilen: Klick und Drag & Drop

    private void Row_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        _pressedRow = Ctx<AppRowViewModel>(sender);
    }
    private AppRowViewModel? _pressedRow;

    private void Row_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _pressedRow is null || !_vm.CanManageDesktops) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(p.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragRow = _pressedRow;
        _pressedRow = null;
        try { DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(AppRowViewModel), _dragRow), DragDropEffects.Move); }
        finally
        {
            _dragRow = null;
            foreach (var c in _vm.Cards) c.IsDropTarget = false;
        }
    }

    private void Row_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_pressedRow is null || !ReferenceEquals(_pressedRow, Ctx<AppRowViewModel>(sender))) return;
        _pressedRow = null;
        _vm.ActivateAppCommand.Execute(Ctx<AppRowViewModel>(sender));
        e.Handled = true;
    }

    private static AppRowViewModel? Dragged(DragEventArgs e) => e.Data.GetDataPresent(typeof(AppRowViewModel)) ? e.Data.GetData(typeof(AppRowViewModel)) as AppRowViewModel : null;

    private void Card_DragEnter(object sender, DragEventArgs e) => Card_DragOver(sender, e);

    private void Card_DragOver(object sender, DragEventArgs e)
    {
        var row = Dragged(e);
        var card = Ctx<DesktopCardViewModel>(sender);
        var ok = row is not null && card is not null && row.DesktopId != card.Id;
        e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
        if (card is not null) card.IsDropTarget = ok;
        e.Handled = true;
    }

    private void Card_DragLeave(object sender, DragEventArgs e)
    {
        if (Ctx<DesktopCardViewModel>(sender) is { } card) card.IsDropTarget = false;
    }

    private async void Card_Drop(object sender, DragEventArgs e)
    {
        var row = Dragged(e);
        var card = Ctx<DesktopCardViewModel>(sender);
        if (card is not null) card.IsDropTarget = false;
        if (row is null || card is null) return;
        e.Handled = true;
        await _vm.MoveAppAsync(row, card);
    }

    // ------------------------------------------------------------ Umbenennen (braucht kurzzeitig Tastatur)

    private void RenameBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox tb) return;
        if (tb.IsVisible)
        {
            EnterKeyboardMode();
            Dispatcher.BeginInvoke(() => { tb.Focus(); Keyboard.Focus(tb); tb.SelectAll(); }, DispatcherPriority.Input);
        }
        else if (!_vm.Cards.Any(c => c.IsEditing)) LeaveKeyboardMode();
    }

    private void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (Ctx<DesktopCardViewModel>(sender) is not { } card) return;
        if (e.Key == Key.Enter) { _vm.CommitRenameCommand.Execute(card); e.Handled = true; }
        else if (e.Key == Key.Escape) { _vm.CancelRenameCommand.Execute(card); e.Handled = true; }
    }

    private void RenameBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (Ctx<DesktopCardViewModel>(sender) is { IsEditing: true } card) _vm.CommitRenameCommand.Execute(card);
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        foreach (var c in _vm.Cards.Where(c => c.IsEditing).ToList()) _vm.CommitRenameCommand.Execute(c);
    }

    private IntPtr _previousForeground;

    private void EnterKeyboardMode()
    {
        if (_keyboardMode) return;
        _keyboardMode = true;
        _previousForeground = GetForegroundWindow();
        SetNoActivate(false);
        Activate();
    }

    private void LeaveKeyboardMode()
    {
        if (!_keyboardMode) return;
        _keyboardMode = false;
        SetNoActivate(true);
        // Fokus an das vorherige Programm zurückgeben
        if (_previousForeground != IntPtr.Zero && _previousForeground != _hwnd) SetForegroundWindow(_previousForeground);
        _previousForeground = IntPtr.Zero;
    }

    private static T? FindParent<T>(DependencyObject? d, Func<T, bool>? predicate = null) where T : DependencyObject
    {
        while (d is not null)
        {
            if (d is T t && (predicate is null || predicate(t))) return t;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }
}
