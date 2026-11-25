namespace DeskRecall.Core.Models;

public sealed class SessionProfile
{
    public const string DefaultName = "Standard";

    public string Name { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public SessionSnapshot? Snapshot { get; set; }
}
