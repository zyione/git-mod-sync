namespace ModSync.Models;

public sealed record GitFileChange(string Path, ChangeType Type);
public sealed record PushNotificationItem(string Category, string Name, ChangeType Type);
