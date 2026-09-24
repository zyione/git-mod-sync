namespace ModSync.Models;

/// <summary>
/// Summary of calculated or applied mod changes.
/// </summary>
public class SyncSummary
{
    public List<ModChange> Changes { get; set; } = new();

    public int AddedCount => Changes.Count(c => c.Type == ChangeType.Added);
    public int UpdatedCount => Changes.Count(c => c.Type == ChangeType.Updated);
    public int RemovedCount => Changes.Count(c => c.Type == ChangeType.Removed);
    public int UnchangedCount => Changes.Count(c => c.Type == ChangeType.Unchanged);
    public int TotalActionableChanges => AddedCount + UpdatedCount + RemovedCount;

    public bool HasChanges => TotalActionableChanges > 0;

    public IEnumerable<ModChange> Added => Changes.Where(c => c.Type == ChangeType.Added);
    public IEnumerable<ModChange> Updated => Changes.Where(c => c.Type == ChangeType.Updated);
    public IEnumerable<ModChange> Removed => Changes.Where(c => c.Type == ChangeType.Removed);
}
