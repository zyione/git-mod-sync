# Complete update: v1.5.1

The user requested Mods Push and Reinstall before publication. The v1.5.0 workflow (37036948875) was canceled successfully before release creation; its tag remains as history. Mods controls were added beside the counter, scoped to Mods only. Unrelated malformed pack tracking no longer blocks mods actions. All 87 tests passed; dashboard and Mods confirmation were inspected in light/dark themes. Publish v1.5.1 only after these completed checks; verify its workflow and assets.

# ModSync continuation record

October 3, 2026: v1.4.1 was published successfully. The requested instance selection, front-screen update check, category verification statuses, and useful error recovery are implemented for v1.5.0.

- First run confirms a detected Minecraft instance; app directory is suggested if it contains game files. Bounded detection checks default/common launcher locations and official launcher custom gameDir profiles. Browse supports custom/portable locations; a new instance must have been launched once to create recognizable game files.
- config.json stores instanceSelectionCompleted and the selected mods path. Existing completed-sync/custom-path configs migrate without repeating onboarding. Missing saved folders prompt for replacement. Choose Instance is on the main screen and Settings; relative pack paths, Fabric information, and backups follow the instance.
- config saves use a temporary file replacement. Failed instance save restores the previous in-memory choice.
- Main screen has Check for Updates. Counters are retained; equal counts do not prove synchronized contents. States include unchecked, checking, changes available, last verified, published, failed, and disabled. Order-only checks never claim pack contents were verified.
- Recovery actions route to Retry, Sign In, Choose Instance, Sync Section, Open Backups, or View Logs. Retries reopen relevant preview/confirmation; no automatic clean reinstall or backup deletion.
- Changing instances clears category verification/Fabric status/backup shortcut and ignores old asynchronous count/Fabric results.
- 86 tests passed, no compiler warnings, git diff --check passed. Instance chooser/dashboard previews inspected in both themes. Release notes: docs/releases/v1.5.0.md. Remaining step is push/tag and verify the release workflow/assets.

Default repositories remain zyione/4stoogies-mod-list, zyione/4stoogies-resourcepack-list, and zyione/4stoogies-shaderpack-list. Resource Pack Push/Sync automatically include order; independent order-only actions remain.

Use C:/Users/PC/.dotnet/dotnet.exe for tests. Whole syncs are not transactions; completed changes may remain after a later failure, with snapshots available for recovery. Discovery does not recursively search the user's entire disk. Push/release after passing checks is authorized by the user.
