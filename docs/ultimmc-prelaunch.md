# UltimMC launch checks and portable setup

## Install and enable

1. Put `ModSync.exe` in `UltimMC\ModSync`, alongside the launcher's `instances` folder. Keep all ModSync files out of the game instance.
2. Open ModSync. Select an instance once; BigChadGuys is suggested when found. **Change instance** on the main screen switches between saved profiles. Repositories, exclusions, content folders, and recovery state follow the selected profile.
3. Review repositories and personal-mod exclusions. If you have not added personal mods, choose **Skip — no personal mods**.
4. Close UltimMC. Choose **Enable** beside **Launch check** and approve setup. ModSync creates its script and backup in its own folder and changes the selected instance's `instance.cfg`.
5. Reopen UltimMC and launch that instance. ModSync checks the launched instance's saved profile and waits for your decision when attention is needed.

Setup is per instance and per player's computer. A healthy installation continues automatically. Updates are previewed before changing Minecraft files. The app still requests administrator access; rejecting Windows' UAC prompt cancels launch.

**Manage** disables the check and restores previous command settings. **Repair** restores missing or outdated generated scripts. Close UltimMC before either action, because an open launcher can overwrite external configuration changes. If someone edited the command outside ModSync, the app refuses to discard that edit; review it in UltimMC or use the saved `instance.cfg.backup`.

Existing effective pre-launch commands run first with the launcher environment; failure cancels launch. Existing wrapper and post-exit commands are preserved. Enabling instance command overrides copies inherited commands into the instance, so later global changes do not propagate until the check is disabled/re-enabled. Existing manual ModSync hooks must be removed before automatic setup to prevent recursion. Unknown settings are retained, including later edits.

## Portable storage and migration

`UltimMC\ModSync` contains the executable, current configuration, saved instance profiles, repository caches, hash cache, launch scripts, logs, tools, update downloads, backups, and incomplete-update markers. Windows Credential Manager remains the primary credential store; the encrypted fallback can only be decrypted by the original Windows user.

Moving the entire UltimMC folder preserves relative instance paths and generated hook paths. Moving only ModSync breaks the hook: restore its location before launching. Instances on another drive may require selecting their new location.

Settings from the previous AppData location are imported when that location belongs to this installation. On first instance selection, a single matching legacy configuration can also be imported from another installation, with its backups and unfinished-update marker. Ambiguous matches are not chosen automatically. Original files are retained. Existing game-folder backups are never moved. Review repository choices after migration.

## Faster checks

Checks query the exact remote branch with `git ls-remote` before transferring repository contents. A healthy cache at that commit needs no fetch, reset, or download. Changed caches fetch once; missing caches clone. Independent repositories are checked concurrently. Categories sharing the same repository URL and branch share a cache/check. Lightweight probes time out after 20 seconds; actual content transfers have a longer timeout.

Verified repository hashes are reused by commit and file metadata. Installed game files are still hashed; applying a preview rechecks full source and destination contents. Dirty caches are repaired before reviewing. A branch that changes during checking requires another check. Account validation and remote count/Fabric queries no longer hold up startup or window focus. App release checks run separately.

## Player experience and recovery

- Personal mods: review exclusions, confirm they are excluded, or skip. New local JAR names trigger the reminder again.
- Updates: review additions, replacements, removals, and exclusions before applying.
- Offline or failed check: **Retry**, **Play anyway**, or **Cancel launch**. An unavailable check is never shown as synchronized.
- Updated and verified: choose whether to close ModSync or keep it open while Minecraft continues.
- Incomplete update: Play anyway is blocked until a full check verifies recovery. Completed changes can remain after a later failure; backups are retained.
- Fabric update: cancel launch, close UltimMC, apply in ModSync, and reopen the launcher to reload its metadata.
- Duplicate launch or update in progress: the extra request is cancelled. Retry once the current operation finishes.
- Known different instance: ModSync loads that instance's saved settings. Unknown instances require one-time selection and review before retrying.
- Missing app/script: UltimMC cancels launch. Restore ModSync or disable its command before deleting the portable folder.

The launch bridge returns independently of the dashboard, so keeping ModSync open does not keep Minecraft waiting. ModSync does not start Minecraft itself. This follows UltimMC's [pre-launch contract](https://github.com/UltimMC/Launcher/blob/develop/launcher/launch/steps/PreLaunchCommand.cpp) and [flat settings format](https://github.com/UltimMC/Launcher/blob/develop/launcher/settings/INIFile.cpp).

## Development validation

Tests use disposable instances and local Git repositories. They cover profile isolation, migration, command preservation and undo, script repair, moved launcher folders, quoted script arguments, failed original commands, unchanged repositories, changed/dirty caches, offline/missing branches, and hash invalidation. WPF renders cover both themes and small windows.

BigChadGuysPlus remains read-only during development and testing. The real instance hook is not installed by development tests, and no real Minecraft launch is performed.
