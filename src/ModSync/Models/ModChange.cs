namespace ModSync.Models;

public enum ChangeType
{
    Added,
    Removed,
    Updated,
    Unchanged
}

/// <summary>
/// Represents a detected difference between local mods folder and the internal repository.
/// </summary>
public class ModChange
{
    public string RelativePath { get; set; } = string.Empty;
    public ChangeType Type { get; set; }
    public ModFileItem? SourceItem { get; set; }
    public ModFileItem? TargetItem { get; set; }

    public string Symbol => Type switch
    {
        ChangeType.Added => "+",
        ChangeType.Removed => "-",
        ChangeType.Updated => "~",
        _ => " "
    };

    public override string ToString() => $"{Symbol} {RelativePath}";
}
