namespace ModSync.Services;

public enum RecoveryAction { Retry, SignIn, ChooseInstance, SyncSection, OpenBackups, ViewLogs }
public sealed record RecoverySuggestion(RecoveryAction Action, string Label, string Hint);
public static class ErrorRecoveryService
{
    public static RecoverySuggestion Suggest(string error, bool canRetry)
    {
        string text = error.ToLowerInvariant();
        if (text.Contains("minecraft is currently running") || text.Contains("used by another process") || text.Contains("sharing violation"))
            return new(RecoveryAction.Retry, "Retry", "Close Minecraft and any program using these files, then retry.");
        if (text.Contains("401") || text.Contains("token") || text.Contains("login is required") || text.Contains("authentication"))
            return new(RecoveryAction.SignIn, "Sign In", "Sign in to GitHub again, then retry the action.");
        if (text.Contains("newer repository changes"))
            return new(RecoveryAction.SyncSection, "Sync Section", "Review and sync this section before publishing your changes.");
        if (text.Contains("instance") || text.Contains("could not find a part of the path"))
            return new(RecoveryAction.ChooseInstance, "Choose Instance", "Check that the correct Minecraft instance is selected.");
        if (text.Contains("access denied") || text.Contains("unauthorizedaccess") || text.Contains("permission"))
            return new(RecoveryAction.ViewLogs, "View Logs", "Check that the instance and app folders are writable, then retry.");
        if (text.Contains("not enough space") || text.Contains("disk full"))
            return new(RecoveryAction.OpenBackups, "Open Backups", "Free disk space before retrying. Keep any backups you still need.");
        if (text.Contains("resourcepack-order") || text.Contains("json") || text.Contains("declared") || text.Contains("checksum") || text.Contains("verification failed"))
            return new(RecoveryAction.ViewLogs, "View Logs", "Inspect the details before retrying. Existing backups remain available.");
        return canRetry ? new(RecoveryAction.Retry, "Retry", "Check your connection and file access, then retry. Completed changes are preserved.")
            : new(RecoveryAction.ViewLogs, "View Logs", "Open the logs for details. Existing files and backups are available for recovery.");
    }
}
