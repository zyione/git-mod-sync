# ModSync UI/UX Design Skill

Use this document as the UI/UX design guide whenever modifying, redesigning, or adding interfaces to ModSync.

The goal is to make ModSync feel like a polished desktop application rather than a collection of buttons and status labels.

Primary design inspirations:

- Modrinth App: https://modrinth.com/app
- GitHub Desktop: https://desktop.github.com/
- GitHub Desktop UI documentation: https://docs.github.com/en/desktop/overview/creating-your-first-repository-using-github-desktop
- Prism Launcher: https://prismlauncher.org/
- Prism Launcher settings reference: https://prismlauncher.org/wiki/help-pages/launcher-settings/

Use Modrinth primarily for Minecraft-related UI organization.

Use GitHub Desktop primarily for synchronization concepts such as:

- local changes
- remote changes
- push
- pull
- sync
- repository status
- contextual actions

Use Prism Launcher for:

- Minecraft instance management
- launcher-level settings
- instance selection
- Minecraft desktop application conventions

---

# 1. Core UX Goal

The main screen should answer these three questions almost immediately:

1. What modpack am I currently managing?
2. Is everything synchronized?
3. What action should I take next?

Do not force the user to interpret raw numbers or technical repository information before understanding the current state.

The user should be able to understand the modpack status within roughly 2-3 seconds.

---

# 2. Main Information Hierarchy

The preferred hierarchy is:

1. Modpack / instance
2. Overall sync status
3. Mods
4. Resource Packs
5. Shaders
6. Fabric Loader
7. Primary actions
8. Secondary navigation/settings

Avoid giving branding more visual prominence than the actual modpack state.

The app name should exist, but the current instance and synchronization state should be more important.

---

# 3. Header

Avoid using too much vertical space for:

# ModSync

Your Minecraft modpack, in sync

After the user has used the application once, this information is not very useful.

Prefer a compact header.

Example:

```text
● ModSync                                      ⚙  ─  □  ×

4Stoogies
Minecraft 1.20.1 · Fabric
C:\Users\PC\Desktop\...
```

Optionally show an overall status underneath:

```text
● 5 local changes    ● 0 conflicts    Checked 2m ago

                                         Check Now
```

The header should communicate useful application state.

---

# 4. Content Cards

Mods, Resource Packs, and Shaders should use the same visual component whenever possible.

They should feel like three instances of the same system rather than three separately designed sections.

Recommended structure:

```text
┌──────────────────────────────────────────────┐
│ MODPACK CONTENT                              │
│                                              │
│ ● Mods                           +5 local    │
│   381 local · 376 repository                │
│   zYione/4stoogies-mod-list                 │
│                              [Sync] [Push 5] │
│                                              │
│ ──────────────────────────────────────────── │
│                                              │
│ ✓ Resource Packs                    Synced   │
│   36 local · 36 repository                  │
│   zYione/4stoogies-resourcepack-list        │
│                                      [•••]   │
│                                              │
│ ──────────────────────────────────────────── │
│                                              │
│ ✓ Shaders                           Synced   │
│   8 local · 8 repository                    │
│   ComplementaryReimagined_r5.9.3.zip        │
│                                      [•••]   │
└──────────────────────────────────────────────┘
```

Keep spacing, button placement, text sizes, and status indicators consistent.

---

# 5. Never Show Ambiguous Numbers

Avoid:

```text
381 / 376 mods
```

This makes the user figure out which number represents local files and which represents the repository.

Prefer:

```text
381 local · 376 repository
```

Even better, provide a human-readable interpretation first:

```text
+5 local changes
381 local · 376 repository
```

Possible states:

```text
✓ Synced
```

```text
↑ 5 local changes
```

```text
↓ 3 remote changes
```

```text
↑ 3 local · ↓ 2 remote
```

```text
⚠ Conflict detected
```

The interpretation should be more visually prominent than the raw counts.

---

# 6. Contextual Sync Actions

Do not always show every synchronization button equally.

Actions should change based on the current state.

This should behave similarly to GitHub Desktop.

Examples:

## Everything is synchronized

```text
✓ Up to date
```

Possible action:

```text
Check Again
```

Do not show unnecessary Push or Pull actions.

---

## Remote repository has new changes

```text
↓ 3 remote changes

[ Pull Changes ]
```

---

## Local installation has changes

```text
↑ 5 local changes

[ Push 5 Changes ]
```

---

## Both sides changed

```text
↓ 2 remote changes    ↑ 3 local changes

[ Sync ]
```

---

## Conflict

```text
⚠ Conflict detected

[ Review Conflict ]
```

Conflicts should not silently perform destructive synchronization.

---

# 7. Primary vs Secondary Actions

Frequently used actions should be visible.

Uncommon actions should not permanently occupy space in the main UI.

Primary actions include things like:

- Sync
- Pull
- Push
- Check for changes

Secondary actions include:

- Open Folder
- View Repository
- Configure Sync Order
- Reinstall
- Repository Settings
- Advanced options

Put secondary actions inside a three-dot menu.

Example:

```text
•••

Open folder
View repository
Configure sync order
Reinstall mods
Repository settings
```

This keeps the main interface clean.

---

# 8. Avoid Tiny Scattered Text Actions

Avoid designs like:

```text
Open Folder    Reinstall...
Sync Order     Push Order    Reinstall...
```

These are hard to scan and can look like website footer links.

Prefer proper contextual menus or small secondary buttons.

Example:

```text
Mods                                   [Sync] [Push 5] [•••]
```

---

# 9. Fabric Loader Status

The existing Fabric Loader section is conceptually strong because it clearly communicates:

- installed version
- required version
- compatibility status

However, matching versions do not need to show redundant information.

Instead of:

```text
Fabric Loader · Minecraft 1.20.1

Installed: 0.19.5 → Required: 0.19.5

✓ Up to date
```

Prefer:

```text
Fabric Loader

Minecraft 1.20.1 · Fabric 0.19.5

                                      ✓ Up to date
```

Only show installed vs required versions when they differ.

Example:

```text
Fabric Loader                         Update required

Installed 0.19.4 → Required 0.19.5

                                      [ Update ]
```

Display additional information primarily when user action is required.

---

# 10. Overall Modpack Actions

Avoid having giant buttons dominate the screen when nothing needs attention.

Current actions such as:

```text
Sync All

Push Modpack
```

should reflect the current state.

Example when changes exist:

```text
5 local changes

┌────────────────────────────────────────────┐
│                  Sync All                  │
└────────────────────────────────────────────┘

                         Push 5 Changes
```

Alternatively:

```text
5 local changes

[ Sync All ]                     [ Push 5 Changes ]
```

When nothing needs action:

```text
✓ Everything is up to date

                                      Check Again
```

The UI should become visually quieter when the system is healthy.

---

# 11. Footer Navigation

Avoid footer designs that look like website links.

Avoid:

```text
Choose Instance     Check for Updates

Check Status • @zyione • Repositories • Settings

Backups      Clean Reinstall...
```

Prefer application navigation.

Example:

```text
⌂ Overview     ◫ Instances     ↻ Repositories     ⏱ Backups     ⚙ Settings
```

If the window becomes too narrow, consider an icon sidebar.

Example:

```text
│ ◎
│
│ ⌂
│ ◫
│ ↻
│ ⏱
│
│ ⚙
```

Do not place dangerous maintenance operations in normal navigation.

---

# 12. Clean Reinstall

"Clean Reinstall" is a destructive or disruptive action.

It should NOT permanently sit in red on the main page.

Move it to something like:

```text
Settings
→ Modpack
→ Maintenance
→ Clean Reinstall
```

Require confirmation before proceeding.

Possible confirmation:

```text
Clean reinstall ModSync instance?

This will rebuild the local modpack installation.

Your backups will not be removed.

[ Cancel ]    [ Clean Reinstall ]
```

Use red only for the destructive confirmation action.

---

# 13. Color System

Use restrained colors.

Suggested conceptual palette:

```text
App Background
#18181B

Surface
#202023

Elevated Surface
#29292D

Border
#36363B
```

Exact colors may be adjusted.

Text hierarchy:

```text
Primary text
~95% white

Secondary text
~65% white

Tertiary text
~45% white
```

Status colors:

```text
Blue
Primary action

Green
Successful / synchronized

Yellow or Amber
Changed / warning / needs attention

Red
Error / destructive action
```

Do not overuse colors.

Most of the UI should remain neutral.

Color should communicate meaning.

---

# 14. Status Indicator Rules

Use status colors consistently.

Examples:

```text
● Green
Synced / healthy / successful
```

```text
● Yellow
Changes detected / attention needed
```

```text
● Red
Error / conflict / failure
```

```text
● Gray
Not checked / unknown / disabled
```

Avoid using color as the only signal.

Always include accompanying text.

Example:

```text
● 5 local changes
```

instead of only showing a yellow dot.

---

# 15. Typography Hierarchy

Use a clear hierarchy.

Example:

```text
App / Instance Name
20-24 px

Section Headings
14-16 px medium/semi-bold

Content Title
13-15 px medium

Primary Body
12-14 px

Secondary Information
11-13 px

Tertiary Metadata
10-12 px
```

Avoid making every label bold.

Use weight to indicate importance.

---

# 16. Spacing System

Use a predictable spacing scale.

Suggested:

```text
4 px
8 px
12 px
16 px
24 px
32 px
```

Most component padding should use:

```text
12 px
16 px
24 px
```

Do not use arbitrary spacing values unless necessary.

Cards should have generous internal padding.

Suggested card padding:

```text
16-20 px
```

Section gap:

```text
12-16 px
```

Large layout gap:

```text
24-32 px
```

---

# 17. Border Radius

Use consistent rounded corners.

Suggested:

```text
Small elements
6-8 px

Buttons
8-10 px

Cards
12-16 px
```

Do not mix many radius styles.

---

# 18. Button Hierarchy

Primary button:

```text
Blue background
High contrast text
```

Example:

```text
[ Sync All ]
```

Secondary button:

```text
Neutral elevated surface
```

Example:

```text
[ Push Changes ]
```

Tertiary action:

```text
Text button or icon
```

Example:

```text
Open Folder
```

Destructive action:

```text
Red
```

Only use destructive styling when something can cause data loss or significant disruption.

---

# 19. Disabled Buttons

Do not make disabled buttons look almost identical to active ones.

A disabled button should clearly communicate that it cannot currently be used.

Reduce:

- contrast
- saturation
- opacity

But keep text readable.

If possible, avoid showing unnecessary disabled actions entirely.

---

# 20. Tooltips

Use tooltips for icons whose meaning is not obvious.

Example:

```text
•••
More actions
```

```text
↻
Check for changes
```

```text
⚙
Settings
```

Do not use tooltips as a replacement for important labels.

---

# 21. Loading States

Never leave the user unsure whether ModSync is working.

For checks:

```text
Checking mods...
```

For synchronization:

```text
Syncing mods...
24 / 381
```

For repository operations:

```text
Fetching repository...
```

For longer operations, show progress when possible.

Avoid indefinite spinners when progress information is available.

---

# 22. Success Feedback

After operations finish, show temporary confirmation.

Example:

```text
✓ Modpack synchronized
```

or:

```text
✓ 5 changes pushed successfully
```

Do not permanently show notification banners for successful operations unless relevant.

---

# 23. Error Handling

Errors should tell the user:

1. What failed
2. Why it likely failed
3. What they can do

Bad:

```text
Sync failed.
```

Better:

```text
Could not sync Mods

The GitHub repository could not be reached.

[ Try Again ]    [ View Details ]
```

Technical details should be available but not forced onto normal users.

---

# 24. Repository Information

Repository names can be displayed but should remain secondary information.

Example:

```text
Mods

↑ 5 local changes

381 local · 376 repository

zYione/4stoogies-mod-list
```

The repository URL should not visually compete with the synchronization status.

---

# 25. File Paths

Long paths should be truncated.

Example:

```text
C:\Users\PC\Desktop\mmc-cracked-win32\UltimM...
```

Allow the full path through:

- tooltip
- copy action
- Open Folder

Do not allow long file paths to break the layout.

---

# 26. Active Shader

Active content should be clearly distinguished from inventory count.

Example:

```text
Shaders

✓ Synced

8 local · 8 repository

Active:
Complementary Reimagined r5.9.3
```

Avoid putting too much information onto one line.

---

# 27. Sync Order

"Sync Order" and "Push Order" are advanced operations.

Do not permanently display these actions beside normal synchronization.

Place them under:

```text
•••
→ Configure order
```

If order changes are important, consider a dedicated modal or screen.

---

# 28. Main Screen Target Design

Aim for a structure similar to this:

```text
┌──────────────────────────────────────────────┐
│ ● ModSync                            ⚙  ─  × │
│                                              │
│ 4Stoogies                                    │
│ Minecraft 1.20.1 · Fabric                    │
│                                              │
│ ● 5 local changes · Checked 2m ago           │
│                                              │
│ ┌──────────────────────────────────────────┐ │
│ │ MODPACK CONTENT                          │ │
│ │                                          │ │
│ │ ● Mods                       +5 local    │ │
│ │   381 local · 376 repository            │ │
│ │   zYione/4stoogies-mod-list             │ │
│ │                         [Sync] [Push 5]  │ │
│ │                                          │ │
│ │ ✓ Resource Packs                Synced   │ │
│ │   36 local · 36 repository              │ │
│ │                                  [•••]   │ │
│ │                                          │ │
│ │ ✓ Shaders                       Synced   │ │
│ │   Complementary Reimagined              │ │
│ │                                  [•••]   │ │
│ └──────────────────────────────────────────┘ │
│                                              │
│ ┌──────────────────────────────────────────┐ │
│ │ Fabric Loader 0.19.5       ✓ Up to date │ │
│ └──────────────────────────────────────────┘ │
│                                              │
│ 5 local changes                              │
│                                              │
│ ┌──────────────────────────────────────────┐ │
│ │                 Sync All                 │ │
│ └──────────────────────────────────────────┘ │
│                                              │
│ ⌂ Overview   ◫ Instances   ⏱ Backups   ⚙    │
└──────────────────────────────────────────────┘
```

This is conceptual.

Do not copy dimensions blindly.

Adapt it to the actual application framework and window size.

---

# 29. Application State Should Drive UI

The interface should render differently depending on application state.

Think in explicit states.

Example:

```text
UNKNOWN
CHECKING
SYNCED
LOCAL_CHANGES
REMOTE_CHANGES
BOTH_CHANGED
CONFLICT
SYNCING
ERROR
```

Each state should define:

- status text
- icon
- color
- available actions
- primary action
- secondary actions

Example:

```text
SYNCED

Icon:
✓

Status:
Up to date

Primary action:
None

Secondary:
Check Again
```

Example:

```text
LOCAL_CHANGES

Icon:
↑

Status:
5 local changes

Primary:
Push 5 Changes

Secondary:
View Changes
```

Example:

```text
REMOTE_CHANGES

Icon:
↓

Status:
3 remote changes

Primary:
Pull Changes

Secondary:
View Changes
```

Example:

```text
CONFLICT

Icon:
⚠

Status:
Conflict detected

Primary:
Review Conflict

Secondary:
View Details
```

---

# 30. Progressive Disclosure

Show basic information first.

Hide advanced information until requested.

Main screen:

```text
Mods
5 local changes
381 local · 376 repository
```

Advanced menu:

```text
Repository URL
Branch
Local directory
Sync order
Ignored files
Reinstall
Debug information
```

This prevents information overload.

---

# 31. Desktop App Philosophy

ModSync is a desktop utility.

It should feel closer to:

- GitHub Desktop
- Modrinth App
- Prism Launcher
- Discord desktop settings

and less like:

- a webpage
- an admin panel
- a form
- a collection of hyperlinks

Prefer:

- cards
- contextual buttons
- menus
- native-feeling navigation
- strong application state

Avoid:

- scattered text links
- excessive dividers
- unnecessary labels
- redundant information

---

# 32. Keep the UI Quiet

A healthy state should visually feel calm.

For example, if everything is synchronized:

```text
✓ Everything is up to date
```

The interface should not still present multiple bright buttons demanding attention.

When something changes:

```text
↑ 5 local changes
```

the relevant action can become more prominent.

The amount of visual emphasis should correspond to how much user attention is required.

---

# 33. Avoid Excessive Confirmation Dialogs

Do not ask for confirmation for safe actions like:

- check status
- open folder
- refresh
- pull when safe

Ask for confirmation for things like:

- Clean Reinstall
- replacing local changes
- deleting files
- overwriting conflicting content
- resetting configuration

---

# 34. Accessibility

Maintain sufficient contrast.

Do not rely exclusively on color.

Example:

Bad:

```text
●
```

Better:

```text
● Synced
```

Buttons should have reasonable click targets.

Target at least approximately:

```text
32-40 px height
```

Important actions should generally be larger.

---

# 35. Icons

Use one consistent icon library.

Do not mix:

- emojis
- Windows symbols
- SVG icons from different libraries
- random Unicode glyphs

Good options include:

- Lucide
- Fluent UI icons
- Heroicons

For a Windows-style desktop utility, Lucide or Fluent icons are good choices.

---

# 36. Animation

Animations should be subtle.

Suitable uses:

- button hover
- menu opening
- status transitions
- loading indicators
- card expansion

Avoid:

- bouncing elements
- dramatic transitions
- long fades
- excessive motion

Typical animation duration:

```text
120-200ms
```

---

# 37. Hover States

Every clickable element should provide feedback.

Buttons:

```text
normal
hover
pressed
disabled
```

Cards should only have hover effects when the card itself is clickable.

Do not make static cards appear clickable.

---

# 38. Keyboard Navigation

Where practical:

- Tab should move through controls
- Enter should activate the focused control
- Escape should close menus/dialogs
- focus outlines should remain visible

Do not remove keyboard focus indicators without replacing them.

---

# 39. Window Size

The current ModSync window is relatively narrow.

Design primarily for approximately this form factor.

Avoid layouts requiring large horizontal space.

Prefer stacked layouts over wide tables.

The UI should still behave correctly if vertically resized.

---

# 40. Responsive Behavior

When horizontal space becomes limited:

Prefer:

```text
[ Sync ]
[ Push ]
```

stacking intelligently rather than compressing text until unreadable.

Long repository names should truncate.

Do not allow buttons to overlap status text.

---

# 41. Navigation Recommendation

A good default navigation system:

```text
Overview
Instances
Repositories
Backups
Settings
```

Possible bottom navigation:

```text
⌂ Overview

◫ Instances

↻ Repositories

⏱ Backups

⚙ Settings
```

Avoid putting one-off actions inside the main navigation.

For example:

"Check for Updates" is an action, not a destination.

---

# 42. Settings Organization

Suggested settings structure:

```text
Settings

General
- Launch behavior
- Theme
- Update checks

Modpack
- Instance location
- Minecraft version
- Loader configuration

Repositories
- Mods repository
- Resource Packs repository
- Shaders repository

Synchronization
- Sync behavior
- Ignore rules
- Sync order

Backups
- Automatic backups
- Backup location
- Retention

Maintenance
- Verify files
- Reinstall
- Clean reinstall

Advanced
- Logs
- Debug mode
- Git configuration
```

---

# 43. Empty States

Never leave an empty card without explanation.

Example:

```text
No shader repository configured

Connect a repository to synchronize shaders.

[ Configure Repository ]
```

---

# 44. First Launch

First launch should guide the user toward the required setup.

Example:

```text
Welcome to ModSync

Choose a Minecraft instance to get started.

[ Choose Instance ]
```

Then:

```text
Connect your mod repositories

Mods
Resource Packs
Shaders
```

Avoid showing the full normal dashboard before configuration is complete.

---

# 45. Copywriting Style

Use short, clear language.

Prefer:

```text
5 local changes
```

instead of:

```text
There are currently 5 modifications detected within your local mod directory.
```

Prefer:

```text
Check Again
```

instead of:

```text
Perform Another Status Check
```

Prefer:

```text
Push Changes
```

instead of:

```text
Upload Modpack Repository Changes
```

---

# 46. Terminology

Pick consistent terminology and never randomly switch between similar words.

Recommended:

```text
Local
Repository
Sync
Push
Pull
Conflict
Instance
Mods
Resource Packs
Shaders
Backup
```

If the app internally uses Git, that does not mean every Git concept needs to be exposed.

Use user-facing Minecraft terminology where possible.

---

# 47. Design Priority Order

When deciding between designs, prioritize:

1. Clarity
2. Current state visibility
3. Correct next action
4. Consistency
5. Simplicity
6. Visual polish
7. Animation

Never sacrifice clarity for aesthetics.

---

# 48. Before Adding New UI

Before adding a new UI element, ask:

1. Does the user need this information all the time?
2. Does this need to be on the main screen?
3. Is this a primary action or advanced action?
4. Can this information be derived automatically?
5. Can this be placed in a contextual menu?
6. Does something similar already exist?
7. Is this state already communicated elsewhere?

Avoid duplicate information.

---

# 49. Before Adding a Button

Ask:

```text
What state makes this action relevant?
```

If an action only matters in one state, do not display it in every state.

Example:

"Push" should primarily appear when local changes exist.

"Pull" should primarily appear when remote changes exist.

"Review Conflict" should only appear when conflicts exist.

---

# 50. Before Finishing Any UI Change

Check:

- Is the current modpack obvious?
- Is the current synchronization state obvious?
- Is the next recommended action obvious?
- Are destructive actions separated?
- Are uncommon actions hidden appropriately?
- Are local vs repository counts labeled?
- Are status colors consistent?
- Are components using consistent spacing?
- Are button sizes consistent?
- Are long paths/repository names handled?
- Are loading/error states implemented?
- Are hover/focus/disabled states implemented?
- Does the interface still work at the current window size?
- Does it still look clean when everything is synchronized?

---

# Final Design Principle

ModSync should behave like a status-driven synchronization tool, not a static control panel.

The UI should continuously communicate:

```text
WHAT YOU HAVE
        ↓
WHAT CHANGED
        ↓
WHAT YOU SHOULD DO
```

Example:

```text
Mods

381 local · 376 repository

↑ 5 local changes

[ Push 5 Changes ]
```

That is significantly clearer than:

```text
Mods · Not checked

381 / 376 mods

[ Sync ] [ Push ]

Open Folder     Reinstall...
```

Whenever redesigning the application, optimize for the first version.

Keep ModSync clean, quiet, consistent, and state-aware.