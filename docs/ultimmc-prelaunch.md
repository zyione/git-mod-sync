# Check the modpack before launching UltimMC

Keep ModSync.exe and `ultimmc-prelaunch.ps1` together in a permanent folder outside
your Minecraft instance. The preview build is in `publish/prelaunch-preview`.
No integration has been installed into BigChadGuysPlus during development.

## One-time setup

1. Open ModSync and choose the exact game folder used by the UltimMC instance
   (usually the instance's `.minecraft` folder). Review repositories and exclusions.
2. In UltimMC, edit that instance, open Settings → Custom Commands, and enable the
   instance's custom command override. Keep any existing commands; do not overwrite
   unrelated pre-launch work without combining and testing it first.
3. Set the pre-launch command to the following, replacing the script path:

   ```text
   powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\Apps\ModSync\ultimmc-prelaunch.ps1"
   ```

   Use a script path with no `$` characters: UltimMC substitutes variables in the
   command before execution. The script reads the launcher's `INST_MC_DIR`
   environment variable; it does not guess the instance from a working directory.
4. Press Play. ModSync opens (or uses its existing dashboard) and checks all enabled
   repositories. A ready installation continues automatically. Updates are reviewed
   before they are applied, with an explicit Play anyway alternative.

ModSync still requests administrator access, so Windows may show a UAC prompt for
the launch bridge. Rejecting that prompt cancels the launch. This setup is local;
each player's installation needs its own command and selected instance.

UltimMC's [pre-launch implementation](https://github.com/UltimMC/Launcher/blob/develop/launcher/launch/steps/PreLaunchCommand.cpp)
waits for the command and treats exit code zero as permission to continue. Nonzero,
crash, or failure to start cancels launch. The script waits only for the bridge
process, so choosing to keep the dashboard open does not hold Minecraft open at
the gate. No Minecraft process is started by ModSync itself.

## What players see

- Personal mods: Review exclusions, Already excluded, or Skip—no personal mods.
  The choice is saved per instance and the reminder returns for new unexcluded
  local JAR filenames. Exclusions can still be edited in Settings.
- Update available: review additions, replacements, removals, and exclusions.
  Applying uses the reviewed files; changed files/settings require another review.
- Check unavailable: Retry, Play anyway, or Cancel launch. Unknown status is never
  called synchronized. A repository moving during a check requires another check.
- Updated and verified: choose to close ModSync or keep it open. During pre-launch,
  both choices release Minecraft after the response is sent to the launcher.
- Interrupted update: a persisted marker prevents Play anyway until a complete
  check verifies the installation. Backups include replaced/removed mods and packs.
- Minecraft/Java running: updates are deferred. The check is deliberately
  conservative about Java processes, including ones without a visible game window.
- Fabric update needed during pre-launch: cancel the current launch, close
  UltimMC, apply the update in ModSync, then reopen UltimMC. Loader metadata must
  be reloaded by the launcher; ordinary content updates can continue in one flow.
- Duplicate launch request or an update already running: the extra request is
  cancelled; finish the first request, then retry in UltimMC.
- Different instance selected: launch is cancelled with instructions to select
  the matching game folder. A launch request never silently changes instances.

## Storage and recovery

Settings, logs, Git tools, update downloads, and repository caches live under
`%LOCALAPPDATA%\ModSync\<installation-key>`. Settings beside an older executable
are imported once, without deleting or editing the old file. Backups and incomplete
update markers live there too and are keyed by instance. Each backup includes a
`restore-paths.json` map to the original files. Use ModSync's Backups button.
Older backups inside an instance remain untouched and can be opened manually.

Game content, managed pack tracking, and approved game/loader settings are still
written to the selected instance during normal approved synchronization. Checks
only read the instance. A complete sync is not a single atomic transaction:
completed changes can remain after a later failure. Keep Minecraft closed while
repairing; restore from a backup or review and retry the update.

The app verifies enabled categories and the configured loader; excluded files are
preserved, not claimed to match the repository. This does not certify that a mod
combination is compatible or that a server accepts personal mods.

## Development validation

Unit/integration tests use disposable folders, including read-only check behavior,
approved snapshot drift, backups, exclusion reminders, and named-pipe launch
decisions. WPF render tests cover both themes and narrow windows. UltimMC's source
contract was inspected; an actual launcher/game run against BigChadGuysPlus was
not performed or configured.

To uninstall the integration, remove only this pre-launch command in UltimMC.
Keep the ModSync folder in place until that command is removed. Renaming or deleting
the executable/script while the hook is enabled intentionally cancels launch.
