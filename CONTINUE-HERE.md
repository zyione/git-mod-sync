# ModSync continuation record

October 3, 2026: v1.4.0 was published successfully. The user's follow-up requires ordinary Resource Pack Push and Sync to include order automatically; v1.4.1 implements this and is ready for release verification.

- Resource Pack Push uploads pack files plus their saved Minecraft order in the same commit, including newly uploaded selected packs. Missing saved selection preserves existing order; explicitly empty selection clears shared activation. Malformed selection aborts before writes.
- Resource Pack Sync, Sync All, and Resource Pack Reinstall apply the shared declaration automatically, regardless of legacy order preferences. Personal selections remain and may override shared packs.
- Push Order / Sync Order remain separate priority-only actions. Push Order excludes unshared personal packs.
- Independent resource/shader reinstalls retain verified backups and category isolation; worlds remain untouched.
- Obsolete order toggles removed from Settings; old config keys are accepted for compatibility but no longer gate order behavior.
- 78 automated tests passed after Settings controls were removed. Final whitespace cleanup is complete; verify release workflow before reporting publication.

Default repositories remain zyione/4stoogies-mod-list, zyione/4stoogies-resourcepack-list, and zyione/4stoogies-shaderpack-list. The user will publish the desired pack order from Minecraft.

Use C:/Users/PC/.dotnet/dotnet.exe for tests. Repository refresh may download pack data even for priority-only operations. Reinstalls are not whole-category transactions; verified snapshots support recovery. Push and release once checks pass are authorized by the user.
