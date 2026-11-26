namespace DeskRecall.Core.Models;

/// <summary>
/// Eingabeverhalten der Desktop-Karte – ohne WPF testbar.
/// Ein neuer Desktop wird mit dem Standardnamen angelegt und nicht automatisch umbenannt:
/// das Widget nimmt keinen Tastaturfokus (WS_EX_NOACTIVATE), ein offenes Textfeld würde sonst stehen bleiben.
/// Umbenennen nur per Doppelklick auf den Namen oder über das Kontextmenü.
/// </summary>
public static class DesktopCardInput
{
    public const bool EnterRenameAfterCreate = false;

    public enum HeaderAction { None, Switch, CommitRename, BeginRename }

    /// <summary>
    /// Klick auf den Desktop-Namen. Ein Doppelklick öffnet die Umbenennung.
    /// Ab 1.2.0 wird ein Einfachklick auf eine andere Karte nicht mehr um die Doppelklick-Zeit verzögert (siehe <see cref="NameClickSwitchesImmediately"/>).
    /// Während der Umbenennung wird der Name übernommen, der Desktop wechselt nicht.
    /// </summary>
    public static HeaderAction OnNameClick(bool isEditing, bool isDoubleClick, bool clickOnRenameBox, bool clickOnButton)
    {
        if (clickOnButton) return HeaderAction.None;
        if (isDoubleClick) return isEditing || clickOnRenameBox ? HeaderAction.None : HeaderAction.BeginRename;
        if (clickOnRenameBox) return HeaderAction.None;
        return isEditing ? HeaderAction.CommitRename : HeaderAction.Switch;
    }

    /// <summary>
    /// Klick auf den übrigen Kartenkopf (Nummer, Leerraum, nicht der Name). Wechselt sofort, außer während der Umbenennung:
    /// dann wird nur der Name übernommen.
    /// </summary>
    public static HeaderAction OnHeaderChromeClick(bool isEditing, bool clickOnRenameBox, bool clickOnButton)
    {
        if (clickOnButton || clickOnRenameBox) return HeaderAction.None;
        return isEditing ? HeaderAction.CommitRename : HeaderAction.Switch;
    }

    /// <summary>
    /// Ein Klick auf den Namen einer anderen (nicht aktiven) Karte wechselt sofort – ohne auf einen möglichen
    /// Doppelklick zu warten. Auf der aktiven Karte gibt es nichts zu wechseln; dort bleibt der Doppelklick fürs Umbenennen.
    /// </summary>
    public static bool NameClickSwitchesImmediately(bool cardIsActive) => !cardIsActive;

    public static bool ShouldCommitOnOutsideClick(bool isEditing, bool clickOnRenameBox) =>
        isEditing && !clickOnRenameBox;
}
