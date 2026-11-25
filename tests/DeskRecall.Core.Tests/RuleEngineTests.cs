using DeskRecall.Core.Models;
using DeskRecall.Core.Rules;
using Xunit;

namespace DeskRecall.Core.Tests;

public class RuleEngineTests
{
    [Fact]
    public void Matches_process_name_case_insensitive_with_or_without_exe()
    {
        var engine = new RuleEngine(new[] { new AppRule { ProcessName = "Steam.exe", TargetDesktop = 3 } });
        var r = engine.Evaluate(T.W("steam"));
        Assert.Equal(2, r.TargetDesktopIndex);
        Assert.True(r.HasAny);
    }

    [Fact]
    public void Disabled_rules_are_ignored()
    {
        var engine = new RuleEngine(new[] { new AppRule { ProcessName = "steam", TargetDesktop = 3, Enabled = false } });
        Assert.False(engine.Evaluate(T.W("steam")).HasAny);
    }

    [Fact]
    public void First_matching_rule_wins_per_property_later_rules_fill_gaps()
    {
        var engine = new RuleEngine(new[]
        {
            new AppRule { ProcessName = "discord", TargetDesktop = 2 },
            new AppRule { ProcessName = "discord", TargetDesktop = 4, StartState = RuleStartState.Minimized },
        });
        var r = engine.Evaluate(T.W("Discord"));
        Assert.Equal(1, r.TargetDesktopIndex);
        Assert.Equal(WindowShowState.Minimized, r.ForcedState);
        Assert.Equal(2, r.MatchedRules.Count);
    }

    [Fact]
    public void Title_condition_restricts_match()
    {
        var engine = new RuleEngine(new[] { new AppRule { ProcessName = "firefox", TitleContains = "YouTube", TargetDesktop = 3 } });
        Assert.Equal(2, engine.Evaluate(T.W("firefox", "Musik – YouTube — Mozilla Firefox")).TargetDesktopIndex);
        Assert.False(engine.Evaluate(T.W("firefox", "GitHub — Mozilla Firefox")).HasAny);
        // App-Auswertung (ohne Titel) ignoriert Titelregeln
        Assert.False(engine.EvaluateApp("firefox").HasAny);
    }

    [Fact]
    public void NeverAutoStart_and_exclude_flags()
    {
        var engine = new RuleEngine(new[]
        {
            new AppRule { ProcessName = "epicgameslauncher", NeverAutoStart = true },
            new AppRule { ProcessName = "keepass", Exclude = true },
        });
        Assert.True(engine.EvaluateApp("EpicGamesLauncher.exe").NeverAutoStart);
        Assert.True(engine.Evaluate(T.W("KeePass")).Excluded);
    }

    [Fact]
    public void LiveOnly_filters_rules_without_live_flag()
    {
        var engine = new RuleEngine(new[]
        {
            new AppRule { ProcessName = "steam", TargetDesktop = 3, ApplyLive = false },
            new AppRule { ProcessName = "firefox", TargetDesktop = 2, ApplyLive = true },
        });
        Assert.False(engine.Evaluate(T.W("steam"), liveOnly: true).HasAny);
        Assert.Equal(1, engine.Evaluate(T.W("firefox"), liveOnly: true).TargetDesktopIndex);
    }

    [Fact]
    public void Rules_are_read_live_from_delegate()
    {
        var list = new List<AppRule>();
        var engine = new RuleEngine(() => list);
        Assert.False(engine.Evaluate(T.W("steam")).HasAny);
        list.Add(new AppRule { ProcessName = "steam", TargetDesktop = 1 });
        Assert.Equal(0, engine.Evaluate(T.W("steam")).TargetDesktopIndex);
    }
}
