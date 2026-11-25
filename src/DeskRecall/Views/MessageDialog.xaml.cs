using System.Windows;
using System.Windows.Input;

namespace DeskRecall.Views;

/// <summary>Bestätigungs- und Eingabedialog im DeskRecall-Design.</summary>
public partial class MessageDialog : Window
{
    private MessageDialog()
    {
        InitializeComponent();
    }

    public static bool ShowConfirm(string title, string message, string ok, string? cancel, bool danger, IReadOnlyList<string>? details)
    {
        var d = new MessageDialog();
        d.TitleText.Text = title;
        d.MessageText.Text = message;
        d.OkButton.Content = ok;
        if (danger) d.OkButton.Style = (Style)d.FindResource("DangerButton");
        if (cancel is null) d.CancelButton.Visibility = Visibility.Collapsed; else d.CancelButton.Content = cancel;
        if (details is { Count: > 0 })
        {
            d.DetailsBorder.Visibility = Visibility.Visible;
            d.DetailsText.Text = string.Join("\n", details.Take(40)) + (details.Count > 40 ? $"\n… und {details.Count - 40} weitere" : "");
        }
        return d.ShowDialog() == true;
    }

    public static string? ShowPrompt(string title, string label, string initial, string ok)
    {
        var d = new MessageDialog();
        d.TitleText.Text = title;
        d.MessageText.Text = label;
        d.OkButton.Content = ok;
        d.CancelButton.Content = "Abbrechen";
        d.PromptPanel.Visibility = Visibility.Visible;
        d.InputBox.Text = initial;
        d.Loaded += (_, _) => { d.InputBox.Focus(); d.InputBox.SelectAll(); };
        return d.ShowDialog() == true ? d.InputBox.Text.Trim() : null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void Title_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); }
    private void InputBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) DialogResult = true; }
}
