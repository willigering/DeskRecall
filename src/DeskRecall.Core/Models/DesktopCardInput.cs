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
    /// Klick auf den Desktop-Namen. Ein Doppelklick öffnet nur die Umbenennung und wechselt den Desktop nicht.
    /// Den Einfachklick in der Oberfläche um die Doppelklick-Wartezeit verzögern, sonst würde der erste Klick wechseln.
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

    public static bool ShouldCommitOnOutsideClick(bool isEditing, bool clickOnRenameBox) =>
        isEditing && !clickOnRenameBox;
}
