using DeskRecall.Core.Models;

namespace DeskRecall.Core.Rules;

public interface IRuleEngine
{
    RuleOutcome Evaluate(WindowRecord window, bool liveOnly = false);
    RuleOutcome EvaluateApp(string processName);
}

/// <summary>
/// Wertet Programmregeln aus. Regeln werden in Listenreihenfolge geprüft; die erste passende Regel
/// legt eine Eigenschaft fest, spätere Regeln füllen nur noch nicht gesetzte Eigenschaften.
/// </summary>
public sealed class RuleEngine : IRuleEngine
{
    private readonly Func<IReadOnlyList<AppRule>> _rules;

    public RuleEngine(Func<IReadOnlyList<AppRule>> rules) => _rules = rules;
    public RuleEngine(IReadOnlyList<AppRule> rules) : this(() => rules) { }

    public static bool Matches(AppRule rule, string processName, string? title)
    {
        if (!rule.Enabled) return false;
        var want = AppIdentity.NormalizeProcessName(rule.ProcessName);
        if (want.Length == 0) return false;
        if (!string.Equals(want, AppIdentity.NormalizeProcessName(processName), StringComparison.Ordinal)) return false;
        if (!string.IsNullOrWhiteSpace(rule.TitleContains))
        {
            if (title is null) return false; // Titelbedingung bei App-Auswertung nicht prüfbar
            if (title.IndexOf(rule.TitleContains.Trim(), StringComparison.OrdinalIgnoreCase) < 0) return false;
        }
        return true;
    }

    public RuleOutcome Evaluate(WindowRecord window, bool liveOnly = false) => EvaluateCore(window.ProcessName, window.Title ?? "", liveOnly, ignoreTitleRules: false);

    /// <summary>Regeln für ein Programm unabhängig vom Fenstertitel (z. B. „Nie automatisch starten“).</summary>
    public RuleOutcome EvaluateApp(string processName) => EvaluateCore(processName, null, false, ignoreTitleRules: true);

    private RuleOutcome EvaluateCore(string processName, string? title, bool liveOnly, bool ignoreTitleRules)
    {
        int? desktop = null; string? monitor = null; WindowShowState? state = null;
        bool never = false, exclude = false, live = false;
        var matched = new List<Guid>();

        foreach (var rule in _rules())
        {
            if (ignoreTitleRules && !string.IsNullOrWhiteSpace(rule.TitleContains)) continue;
            if (!Matches(rule, processName, title)) continue;
            if (liveOnly && !rule.ApplyLive) continue;
            matched.Add(rule.Id);
            if (rule.Exclude) exclude = true;
            if (rule.NeverAutoStart) never = true;
            if (rule.ApplyLive) live = true;
            if (desktop is null && rule.TargetDesktop is int d && d >= 1) desktop = d - 1;
            if (monitor is null && !string.IsNullOrWhiteSpace(rule.TargetMonitorId)) monitor = rule.TargetMonitorId;
            if (state is null && rule.StartState != RuleStartState.Unchanged)
                state = rule.StartState switch
                {
                    RuleStartState.Minimized => WindowShowState.Minimized,
                    RuleStartState.Maximized => WindowShowState.Maximized,
                    _ => WindowShowState.Normal,
                };
        }

        if (matched.Count == 0) return RuleOutcome.None;
        return new RuleOutcome
        {
            TargetDesktopIndex = desktop,
            TargetMonitorId = monitor,
            ForcedState = state,
            NeverAutoStart = never,
            Excluded = exclude,
            ApplyLive = live,
            MatchedRules = matched,
        };
    }
}
