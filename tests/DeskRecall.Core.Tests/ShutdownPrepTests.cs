using DeskRecall.Core.Models;
using DeskRecall.Core.Session;
using Xunit;

namespace DeskRecall.Core.Tests;

public class ShutdownPrepTests
{
    [Fact]
    public void Closes_user_apps_once_and_skips_shell_and_itself()
    {
        var chrome = T.W("chrome", "Seite", handle: 10);
        var chromeAgain = T.W("chrome", "Seite", handle: 10);
        var teams = T.W("ms-teams", "Chat", handle: 11);
        var edge = T.W("msedge", "Edge", handle: 12);
        var selected = ShutdownPrep.Select(new[]
        {
            chrome, chromeAgain, teams, edge,
            T.W("explorer", "Dokumente", handle: 20, kind: AppKind.Explorer),
            T.W("DeskRecall", "DeskRecall", handle: 21),
            T.W("ApplicationFrameHost", "Host", handle: 22),
            T.W("dwm", handle: 23),
            T.W("code", handle: 0),
            T.W("", "leer", handle: 24),
        });

        Assert.Equal(new long[] { 10, 11, 12 }, selected.Select(w => w.Handle).ToArray());
    }

    [Fact]
    public void Grace_leaves_room_for_a_save_dialog()
        => Assert.InRange(ShutdownPrep.CloseGrace.TotalSeconds, 8, 20);
}
