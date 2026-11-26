using DeskRecall.Core.Models;

namespace DeskRecall.Core.Restore;

/// <summary>Beim Systemstart gilt der benannte Snapshot. Ohne Snapshot bleibt es bei der letzten Sitzung.</summary>
public static class StartupChoice
{
    public static SessionSnapshot? Select(SessionSnapshot? startupProfile, SessionSnapshot? lastSession)
    {
        if (startupProfile is null) return lastSession;
        if (startupProfile.Windows.Count == 0 && startupProfile.Desktops.Count == 0) return lastSession;
        return startupProfile;
    }
}
