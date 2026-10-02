# ModSync v1.4.0 continuation record

Resumed on October 3, 2026 after the user's pause. Implementation and local verification are complete; release publication is being verified.

- Resource Packs and Shaders have independent Reinstall actions with full selected-folder/settings backups; matching shared files are replaced and personal files remain.
- Sync Order applies shared resourcepack-order.txt to installed packs, with highest-first converted to Minecraft's stored order. It leaves pack contents untouched, backs up changed options, and rejects missing required local packs.
- Push Order publishes only the saved Minecraft selection for already shared, locally available packs. It ignores the automatic publishing toggle, excludes personal/built-in packs, refreshes an outdated repository cache without changing installed files/selection, and blocks previous unfinished commits.
- Normal pack sync and reinstall apply priority automatically when order enforcement is enabled. Personal selections are preserved and can override shared packs.
- Settings Clean Reinstall affects mods only; main Modpack reinstall retains combined behavior.
- 76 automated tests passed; light/dark dashboards and scoped confirmation sheets were inspected. git diff --check passed.
- Local version fields are 1.4.0. Release notes: docs/releases/v1.4.0.md. Release pipeline runs tests again and verifies delta reconstruction before publication.

Default repositories remain zyione/4stoogies-mod-list, zyione/4stoogies-resourcepack-list, and zyione/4stoogies-shaderpack-list. The user will publish the desired resource-pack order from Minecraft using the new Push Order action; no arbitrary order was seeded.

Important limits: repository refresh may download pack data even for order-only actions. Reinstalls are not whole-category transactions; retained verified backups support recovery if a later write fails. Worlds and other categories remain untouched. Use C:/Users/PC/.dotnet/dotnet.exe for tests on this host.
