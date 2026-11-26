using System.Collections.Concurrent;
using System.Windows.Automation;
using DeskRecall.Core.Browsers;

namespace DeskRecall.Services;

/// <summary>
/// Liest Browser-Tabs über UI Automation (TabItem-Elemente der Tableiste) – für Chromium-Browser (Chrome, Edge, Brave …)
/// und Firefox. Sucht die Tableiste einmal (Breitensuche, ohne in Webinhalte abzusteigen) und merkt sie sich pro Fenster.
/// Nie auf dem UI-Thread aufrufen.
/// </summary>
public sealed class UiaTabReader : ITabReader
{
    const int MaxDepth = 14;
    const int MaxNodes = 400;
    static readonly Condition TabItemCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem);

    readonly ConcurrentDictionary<long, AutomationElement> _strips = new();

    static CacheRequest NewRequest()
    {
        var c = new CacheRequest { TreeScope = TreeScope.Element | TreeScope.Children };
        c.Add(AutomationElement.ControlTypeProperty);
        c.Add(AutomationElement.NameProperty);
        c.Add(SelectionItemPattern.IsSelectedProperty);
        return c;
    }

    public IReadOnlyList<BrowserTab>? ReadTabs(long hwnd, BrowserFamily family)
    {
        if (hwnd == 0 || family == BrowserFamily.None) return null;
        try
        {
            using (NewRequest().Activate())
            {
                if (_strips.TryGetValue(hwnd, out var strip))
                {
                    try
                    {
                        var fresh = strip.GetUpdatedCache(NewRequest());
                        var tabs = TabsOf(fresh);
                        if (tabs.Count > 0) return tabs;
                    }
                    catch { }
                    _strips.TryRemove(hwnd, out _);
                }

                var found = FindStrip((IntPtr)hwnd);
                if (found is null) return Array.Empty<BrowserTab>();
                _strips[hwnd] = found;
                return TabsOf(found);
            }
        }
        catch
        {
            _strips.TryRemove(hwnd, out _);
            return null;
        }
    }

    public bool SelectTab(long hwnd, BrowserFamily family, BrowserTab tab)
    {
        try
        {
            AutomationElement? strip;
            using (NewRequest().Activate())
            {
                if (!_strips.TryGetValue(hwnd, out strip)) strip = FindStrip((IntPtr)hwnd);
            }
            if (strip is null) return false;
            var items = strip.FindAll(TreeScope.Children, TabItemCondition);
            AutomationElement? target = null;
            if (tab.Index >= 0 && tab.Index < items.Count && SameName(items[tab.Index], tab.Title)) target = items[tab.Index];
            target ??= items.Cast<AutomationElement>().FirstOrDefault(e => SameName(e, tab.Title));
            if (target is null) return false;

            if (target.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sel)) { ((SelectionItemPattern)sel).Select(); return true; }
            if (target.TryGetCurrentPattern(InvokePattern.Pattern, out var inv)) { ((InvokePattern)inv).Invoke(); return true; }
            return false;
        }
        catch { _strips.TryRemove(hwnd, out _); return false; }
    }

    public void Forget(long hwnd) => _strips.TryRemove(hwnd, out _);

    static bool SameName(AutomationElement e, string title)
    {
        try { return string.Equals(e.Current.Name, title, StringComparison.Ordinal); } catch { return false; }
    }

    static List<BrowserTab> TabsOf(AutomationElement strip)
    {
        var list = new List<BrowserTab>();
        foreach (AutomationElement child in strip.CachedChildren)
        {
            if (child.Cached.ControlType != ControlType.TabItem) continue;
            var selected = child.GetCachedPropertyValue(SelectionItemPattern.IsSelectedProperty, true) is bool b && b;
            list.Add(new BrowserTab(list.Count, child.Cached.Name ?? "", selected));
        }
        return list;
    }

    /// <summary>Breitensuche nach dem Element, dessen Kinder TabItems sind. Dokumente (Webseiten) werden übersprungen.</summary>
    static AutomationElement? FindStrip(IntPtr hwnd)
    {
        var root = AutomationElement.FromHandle(hwnd);
        var queue = new Queue<(AutomationElement El, int Depth)>();
        queue.Enqueue((root, 0));
        var visited = 0;
        var req = NewRequest();
        while (queue.Count > 0 && visited < MaxNodes)
        {
            var (el, depth) = queue.Dequeue();
            visited++;
            AutomationElementCollection children;
            try { children = el.CachedChildren; }
            catch { continue; }
            var hasTab = false;
            foreach (AutomationElement c in children)
                if (c.Cached.ControlType == ControlType.TabItem) { hasTab = true; break; }
            if (hasTab) return el;
            if (depth >= MaxDepth) continue;
            foreach (AutomationElement c in children)
            {
                var ct = c.Cached.ControlType;
                if (ct == ControlType.Document) continue;
                try { queue.Enqueue((c.GetUpdatedCache(req), depth + 1)); } catch { }
            }
        }
        return null;
    }
}
