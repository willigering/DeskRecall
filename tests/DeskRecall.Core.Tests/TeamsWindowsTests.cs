using DeskRecall.Core.Apps;
using DeskRecall.Core.Geometry;
using Xunit;

namespace DeskRecall.Core.Tests;

public class TeamsWindowsTests
{
    [Theory]
    [InlineData("Microsoft Teams", true)]
    [InlineData("(3) Microsoft Teams", true)]
    [InlineData("● Microsoft Teams", true)]
    [InlineData("Microsoft Teams (Vorschau)", true)]
    [InlineData("Teams", true)]
    [InlineData("Chat", false)]
    [InlineData("Raum", false)]
    [InlineData("Daily | Microsoft Teams", false)]
    public void Recognizes_the_main_window_title(string title, bool expected)
        => Assert.Equal(expected, TeamsWindows.IsMainTitle(title));

    [Fact]
    public void Prefers_the_shell_title_over_a_larger_side_window()
    {
        var chat = T.W("ms-teams", "Chat", bounds: RectI.FromSize(0, 0, 900, 700));
        var room = T.W("ms-teams", "Raum", bounds: RectI.FromSize(0, 0, 1600, 900));
        var main = T.W("ms-teams", "(2) Microsoft Teams", bounds: RectI.FromSize(0, 0, 400, 300));
        Assert.Same(main, TeamsWindows.ChooseMain(new[] { chat, room, main }));
    }

    [Fact]
    public void Call_and_chat_are_not_a_main_window()
    {
        var chat = T.W("ms-teams", "Chat", bounds: RectI.FromSize(0, 0, 500, 400));
        var room = T.W("Teams", "Raum", bounds: RectI.FromSize(0, 0, 800, 600));
        Assert.Null(TeamsWindows.ChooseMain(new[] { chat, room }));
        var (kept, omitted) = TeamsWindows.OmitUnrestorable(new[] { chat, room, T.W("code", "Editor") });
        Assert.Equal(new[] { "code" }, kept.Select(w => w.ProcessName).ToArray());
        Assert.Equal(2, omitted.Count);
    }
}
