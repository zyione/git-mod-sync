# ModSync v1.6.0

October 3, 2026: the user authorized applying the supplied UI/UX guide, pushing the work, and publishing a release. The guide is now ModSyncUI_UI_SKILL.md; AGENTS.md requires it for all frontend changes.

The dashboard leads with the instance and overall status. Content cards share styling and labeled counts, use Check/Review changes actions, and place push/reinstall/order controls in menus. Navigation and progress remain visible while content scrolls. Settings contains account sign-in and full-modpack maintenance. Desktop sync always previews changes; the redundant desktop sync-confirmation toggle was removed.

The sync engine compares differences, not three-way history. Do not infer local changes, remote changes, or conflicts from counts. Pending repository or internal metadata changes must not appear synchronized. Equal counts do not prove equal contents. Order-only operations do not verify pack files.

Validation: all 87 tests pass in Release, including WPF render checks for both themes, changed/healthy/busy states, menus, dialogs, and reduced window sizes. UI previews are generated in the ignored tests output folder. Release notes: docs/releases/v1.6.0.md. Pushing tag v1.6.0 triggers the Windows build and release workflow.

Use C:/Users/PC/.dotnet/dotnet.exe for builds and tests. Default repositories remain zyione/4stoogies-mod-list, zyione/4stoogies-resourcepack-list, and zyione/4stoogies-shaderpack-list. Whole syncs and multi-repository pushes are not transactions; completed changes may remain after a later failure, with backups available for recovery.
