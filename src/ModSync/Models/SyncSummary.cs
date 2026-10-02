namespace ModSync.Models;

/// <summary>
/// Summary of calculated or applied mod changes.
/// </summary>
public class SyncSummary
{
    public List<ModChange> Changes { get; set; } = new();
    public List<string> PendingRepositories { get; set; } = new();

    public int AddedCount => Added.Count();
    public int UpdatedCount => Updated.Count();
    public int RemovedCount => Removed.Count();
    public int UnchangedCount => Changes.Count(c => c.Type == ChangeType.Unchanged);
    public int IgnoredCount => Changes.Count(c => c.Type == ChangeType.Ignored);
    public int TotalActionableChanges => AddedCount + UpdatedCount + RemovedCount;

    public bool HasChanges => PendingRepositories.Count > 0 || TotalActionableChanges > 0 || Changes.Any(c => c.IsInternal && c.Type != ChangeType.Unchanged);

    public IEnumerable<ModChange> Added => Changes.Where(c => !c.IsInternal && c.Type == ChangeType.Added);
    public IEnumerable<ModChange> Updated => Changes.Where(c => !c.IsInternal && c.Type == ChangeType.Updated);
    public IEnumerable<ModChange> Removed => Changes.Where(c => !c.IsInternal && c.Type == ChangeType.Removed);
    public IEnumerable<ModChange> Ignored => Changes.Where(c => c.Type == ChangeType.Ignored);
}
