# ModSync project instructions

## Protected development instance

Treat `C:\Users\PC\Desktop\mmc-cracked-win32\UltimMC\instances\BigChadGuysPlus`
and all descendants as read-only during development and testing. Do not place builds,
logs, backups, scripts, settings, or test fixtures there, or launch tools that write
there, unless the user explicitly authorizes that specific change. Use disposable
instances elsewhere. This does not prohibit normal player-approved synchronization
by the delivered application.

## Frontend and UI changes

Treat `ModSyncUI_UI_SKILL.md` in the repository root as the source of truth for all frontend/UI changes. Read it before modifying, redesigning, or adding interfaces, and validate the result against its "Before Finishing Any UI Change" checklist.

Follow its guidance for layout, components, synchronization states and actions, navigation, copy, accessibility, and visual styling.
