namespace DeskRecall.Core.Models;

public sealed class DesktopInfo
{
    public Guid Id { get; set; }
    /// <summary>0-basierte Position in der Desktop-Reihenfolge.</summary>
    public int Index { get; set; }
    public string Name { get; set; } = "";

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"Desktop {Index + 1}" : Name;
    public override string ToString() => $"{Index + 1}: {DisplayName} ({Id})";
}
