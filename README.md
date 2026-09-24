# ModSync

**ModSync** is a lightweight, zero-configuration Windows application for synchronizing a Minecraft `mods/` directory with an authoritative GitHub repository.

Designed specifically for non-technical players, ModSync requires no Git knowledge or manual terminal commands, while providing mod pack administrators with safe, one-click push capabilities and secure credential storage.

---

## Table of Contents

- [Key Features](#key-features)
- [Directory Layout](#directory-layout)
- [Project Architecture](#project-architecture)
- [Building and Publishing](#building-and-publishing)
- [Configuration (`config.json`)](#configuration-configjson)
- [Instructions for Players](#instructions-for-players)
- [Personal Mod Exclusions (Ignoring Mods)](#personal-mod-exclusions-ignoring-mods)
- [Auto-Updating ModSync](#auto-updating-modsync)
- [Instructions for Admins](#instructions-for-admins)
- [Setting Up the GitHub Repository](#setting-up-the-github-repository)
- [GitHub File Size Limits and Git LFS](#github-file-size-limits-and-git-lfs)
- [Troubleshooting & FAQ](#troubleshooting--faq)

---

## Key Features

- **Apple-Inspired Graphical Interface:** Soft, spacious, minimalist UI featuring clean typography, rounded cards, subtle hover states, and smooth sheet modals.
- **Automatic Light/Dark Mode:** Adapts seamlessly to your Windows theme with custom Apple dark mode and light mode palettes.
- **Zero-Terminal Experience:** Players simply click **Sync Mods**. No Git installation, command prompt, or complex setup required.
- **Self-Contained Single-File Executable:** Compiles to a single standalone `ModSync.exe` with bundled .NET runtime and automated portable Git provisioning (MinGit).
- **Safe Bidirectional Synchronization:**
  - **Sync (Download):** GitHub Remote $\to$ Internal Repository $\to$ `../mods`
  - **Push (Upload):** `../mods` $\to$ Internal Repository $\to$ GitHub Remote
- **Non-Destructive File Protection:** Only synchronizes specified mod files (`*.jar`). Never deletes unrelated files such as `README.txt`, configs, or `disabled-mods/`.
- **SHA-256 Checksums:** Compares mod files by cryptographic content hash rather than relying solely on filenames or timestamps.
- **Atomic File Updates:** Copies incoming mods to `.tmp` files before renaming/replacing to prevent corrupted partial downloads. Includes automatic retry logic for locked files.
- **Active Minecraft Detection:** Warns users if Java/Minecraft or popular launchers (`javaw.exe`, `MinecraftLauncher.exe`) are currently open.
- **Automatic Application Updates:** In-app updater checks for new releases on GitHub, previews release notes and file sizes, streams downloads with live progress, and cleanly restarts ModSync via an atomic self-deleting batch process.
- **Personal Mod Exclusions & Ignore Rules:** Keep personal mods (e.g., mini-maps, shaders, client performance mods) without them being removed during Sync or uploaded during Push. Alternatively, ignore synced mods you don't want downloaded to your machine. Supports glob patterns (`*`, `?`), `.jar` name matching, and `.modignore` files.
- **Enterprise-Grade Security:** Tokens are stored exclusively in **Windows Credential Manager** (or DPAPI). Plaintext passwords and tokens are never written to `config.json` or logs.

---

## Directory Layout

```text
MinecraftPack/
│
├── mods/
│   ├── fabric-api-0.92.0+1.20.1.jar
│   ├── sodium-fabric-0.5.8+mc1.20.1.jar
│   ├── lithium-fabric-mc1.20.1-0.11.2.jar
│   ├── options.txt              <-- Protected, untouched by ModSync
│   └── disabled-mods/           <-- Protected, untouched by ModSync
│
└── ModSync/
    ├── ModSync.exe              <-- Single standalone GUI executable
    ├── config.json              <-- Configuration file
    │
    ├── repository/              <-- Automatically created internal clone
    │   ├── .git/
    │   └── ...
    │
    ├── logs/                    <-- Daily operation logs
    │   └── modsync-2026-09-24.log
    │
    └── tools/                   <-- Auto-provisioned portable MinGit (if needed)
        └── git/
```

---

## Project Architecture

```text
git-mod-sync/
├── ModSync.sln                          # Visual Studio / .NET Solution
├── config.json                          # Default configuration template
├── .gitignore                           # Git ignore rules
├── README.md                            # Documentation
└── src/
    └── ModSync/
        ├── ModSync.csproj               # .NET 8 WPF Project file
        ├── App.xaml / App.xaml.cs       # Application lifecycle and theme setup
        ├── MainWindow.xaml / .cs        # Apple-inspired minimalist Windows UI
        │
        ├── Themes/
        │   └── ThemeManager.cs          # Windows system Light/Dark theme manager
        │
        ├── Models/
        │   ├── AppConfig.cs             # Configuration schema (repositories, ignore list, update settings)
        │   ├── ModFileItem.cs           # Mod file metadata (path, size, SHA-256)
        │   ├── ModChange.cs             # Change item (Added, Removed, Updated, Ignored)
        │   ├── SyncSummary.cs           # Aggregated change report with ignored breakdown
        │   ├── UpdateInfo.cs            # GitHub Releases update payload & release notes
        │   └── GitStatusInfo.cs         # Repository status model
        │
        ├── Services/
        │   ├── ConfigService.cs         # Config loading, auto-generation, validation
        │   ├── LoggingService.cs        # Thread-safe sanitized daily file logger
        │   ├── MinecraftCheckService.cs # Best-effort running Minecraft detector
        │   ├── ModIgnoreService.cs      # Personal mod exclusions & wildcard pattern matcher
        │   ├── UpdateService.cs         # GitHub Releases version checking & atomic self-updater
        │   ├── IGitService.cs           # Git operation abstraction
        │   ├── GitService.cs            # Git engine with MinGit auto-fallback
        │   ├── AuthenticationService.cs # Token validation and Windows Credential Manager
        │   └── ModSyncService.cs        # Core synchronization, clean reinstall & atomic engine
        │
        └── Utils/
            ├── HashUtils.cs             # Streaming SHA-256 calculations
            ├── PathUtils.cs             # BaseDirectory-relative path resolution
            ├── CredentialUtils.cs       # Windows Credential Manager (Advapi32) + DPAPI
            └── ConsoleUI.cs             # Styling helpers and text formatting
```

---

## Building and Publishing

### Prerequisites
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Windows 10/11 x64

### Compilation Command

To produce a single, self-contained `ModSync.exe` requiring **no** pre-installed .NET runtime on client machines, run:

```powershell
dotnet publish src/ModSync/ModSync.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -o ./publish
```

The resulting `publish/` directory contains:
```text
publish/
├── ModSync.exe
└── config.json
```

Users only need these two files.

---

## Configuration (`config.json`)

If `config.json` is missing, `ModSync.exe` will automatically generate it on startup with the default repository:

```json
{
  "repository": "https://github.com/zyione/4stoogies-mod-list.git",
  "savedRepositories": [
    "https://github.com/zyione/4stoogies-mod-list.git"
  ],
  "branch": "main",
  "modsFolder": "./mods",
  "repositoryFolder": "./repository",
  "requireConfirmationBeforePush": true,
  "requireConfirmationBeforeSync": true,
  "allowedExtensions": [
    ".jar"
  ],
  "syncSubdirectories": false,
  "warnFileSizeMb": 50,
  "maxFileSizeMb": 100,
  "cleanInstallOnFirstRun": true,
  "backupBeforeClean": true,
  "firstSyncCompleted": false,
  "ignoredMods": [
    "OptiFine*",
    "replaymod*.jar",
    "voicechat*"
  ],
  "autoCheckUpdates": true,
  "appUpdateRepository": "https://github.com/zyione/git-mod-sync"
}
```

### Options Description

| Key | Type | Default | Description |
|---|---|---|---|
| `repository` | string | `"https://github.com/zyione/4stoogies-mod-list.git"` | HTTPS Git clone URL of the mod repository |
| `savedRepositories` | array | `["..."]` | List of previously used / saved repository URLs for easy switching |
| `branch` | string | `"main"` | Target Git branch to track |
| `modsFolder` | string | `"./mods"` | Path to the Minecraft `mods` directory (relative to `ModSync.exe`) |
| `repositoryFolder` | string | `"./repository"` | Path to internal Git clone directory |
| `requireConfirmationBeforeSync` | bool | `true` | When `true`, displays update summary and prompts confirmation before applying |
| `requireConfirmationBeforePush` | bool | `true` | When `true`, displays changes and prompts confirmation before pushing to GitHub |
| `allowedExtensions` | array | `[".jar"]` | File extensions managed by ModSync. Non-matching files are never touched |
| `syncSubdirectories` | bool | `false` | When `false`, only checks top-level `.jar` files in `mods/` |
| `warnFileSizeMb` | integer | `50` | Displays a warning if any mod exceeds this size |
| `maxFileSizeMb` | integer | `100` | Aborts push if any mod exceeds GitHub's hard file limit |
| `cleanInstallOnFirstRun` | bool | `true` | Prompts for clean install on brand-new installs |
| `backupBeforeClean` | bool | `true` | Automatically backs up existing mods before performing a clean reinstall |
| `ignoredMods` | array | `[]` | List of mod filenames or wildcard patterns (`*`, `?`) to ignore/exclude |
| `autoCheckUpdates` | bool | `true` | Automatically checks for new `ModSync.exe` releases on launch |
| `appUpdateRepository` | string | `"https://github.com/zyione/git-mod-sync"` | GitHub repository URL used for ModSync application self-updates |

---

## Instructions for Players

Normal players who only download/synchronize mods **do not need a GitHub account**.

1. Download `ModSync.exe` and `config.json`.
2. Place them in your Minecraft folder next to `mods`:
   ```text
   MinecraftInstance/
   ├── mods/
   └── ModSync/
       ├── ModSync.exe
       └── config.json
   ```
3. Double-click `ModSync.exe`.
4. Click **Sync Mods**:
   - ModSync inspects your mods folder, compares cryptographic SHA-256 hashes against GitHub, and brings your folder in sync with the repository.
   - If confirmation is enabled, an Apple-inspired review sheet displays the exact added, updated, and removed files before applying.

### Fresh "Clean Reinstall" (For Messed-Up Folders)

If a player's folder has broken, outdated, or conflicting jars and they want a 100% clean reset:
1. In ModSync, click **Settings** (or the `⚙` gear icon in the header).
2. Under **Maintenance & Tools**, click **Reinstall...** next to **Clean Reinstall Mods**.
3. Review the confirmation sheet:
   - Existing `.jar` files are automatically moved into a safety backup: `../mods_backup_YYYY-MM-DD_HHmmss/`.
   - The `.jar` files in `../mods/` are cleared.
   - Authoritative mods are freshly downloaded and copied from the repository.
   - Non-mod files (personal configs, shaders, options) are preserved and never deleted.
4. Click **Reinstall & Backup**. A notification will confirm completion, with an **Open Backup** button to jump directly to your backed-up files in Windows File Explorer.

---

## Personal Mod Exclusions (Ignoring Mods)

ModSync features complete **bidirectional ignore support**, allowing players and admins to maintain personal mod customizations without interference.

### Use Cases:
1. **Personal Client Mods You Want to Keep:**
   - Keep client-only mods (such as Mini-maps, ReplayMod, custom HUDs, or shaders) in your local `mods/` directory.
   - When you click **Sync Mods**, ModSync will **never delete** these ignored mods.
   - If an admin clicks **Push Mods**, ModSync will **never upload** your ignored personal mods to GitHub.
   - Even during a **Clean Reinstall**, personal ignored mods are safely preserved in place.

2. **Unwanted Synced Mods:**
   - If the repository contains a mod you don't want or can't run on your hardware (e.g., a heavy shader mod or high-res texture pack jar), add it to your ignore list.
   - When you click **Sync Mods**, ModSync will **skip downloading** it to your PC.
   - When pushing changes, ModSync will **not delete** the mod from GitHub.

### Supported Pattern Syntax:
- **Wildcards (`*` and `?`):**
  - `replaymod*` matches `replaymod-1.20.1-2.6.14.jar`
  - `*OptiFine*` matches any OptiFine version
  - `zoomify-?.?.?.jar` matches single-character wildcards
- **Exact or Extension-Less:**
  - `sodium-fabric-0.5.8+mc1.20.1.jar`
  - `sodium-fabric-0.5.8+mc1.20.1` (automatically matches `.jar`)
- **Case-Insensitive:** `optifine*` matches `OptiFine_HD.jar`.

### How to Configure Ignored Mods:
- **In the GUI:**
  1. Click **Settings** (gear icon in the top right).
  2. Under **Ignored Mods & Exclusions**, click **Manage...**.
  3. Enter any custom pattern or click **Exclude** next to any detected mod.
- **In `config.json`:**
  ```json
  "ignoredMods": [
    "OptiFine*",
    "replaymod*",
    "simple-voice-chat*"
  ]
  ```
- **Via a `.modignore` File:**
  - Place a `.modignore` text file in either your `ModSync/` directory or your `mods/` directory with one pattern per line:
    ```text
    # Personal client mods
    replaymod*
    OptiFine*
    custom-hud-*.jar
    ```

---

## Auto-Updating ModSync

ModSync includes an integrated self-updater that tracks official releases from GitHub.

- **Background Checks:** If enabled in Settings (`autoCheckUpdates: true`), ModSync silently checks for newer versions on startup.
- **Non-Intrusive Notification:** When an update is discovered, a pill banner appears in the top navigation bar (`Update Available: v1.1.0 • Review`).
- **Release Preview:** Clicking the update banner or **Check Updates** in Settings opens an Apple-styled sheet showing:
  - Current vs. New Version numbers.
  - Release size and download status.
  - Full release notes and changelog from GitHub.
- **One-Click Update Process:**
  1. Clicking **Update & Restart** downloads the latest `ModSync.exe` directly from GitHub Releases.
  2. ModSync generates a lightweight, self-deleting helper script (`update_modsync.bat`).
  3. The script waits for the running instance to close, atomically replaces `ModSync.exe`, restarts the new version, and cleans itself up.

---

## Instructions for Admins

Admins who manage the modpack and upload updates to GitHub:

### First-Time Authentication Setup
1. Open `ModSync.exe`.
2. Select `[4] GitHub Login`.
3. Choose `[1] Sign in with GitHub Personal Access Token (PAT)`.
4. Generate a token on GitHub:
   - Go to [GitHub Tokens (Classic)](https://github.com/settings/tokens) or Fine-Grained Tokens.
   - Grant the `repo` scope (Read and Write access to repository contents).
5. Paste the token into ModSync.
6. The token is verified against the GitHub API and stored in **Windows Credential Manager**. It is never written to disk in plaintext.

### Pushing Mod Updates
1. Place new or updated `.jar` files into `../mods` (or delete unwanted mods).
2. Open `ModSync.exe`.
3. Select `[2] Push Mods`.
4. ModSync will:
   - Check if remote has newer commits first.
   - Verify all file sizes comply with GitHub limits.
   - Show a summary of detected changes (`+ added`, `- removed`, `~ updated`).
   - Ask for confirmation: `Push these mod changes to GitHub? [Y/n]`
   - Automatically commit and push without requiring manual Git commands.

---

## Setting Up the GitHub Repository

1. Go to [github.com/new](https://github.com/new).
2. Name your repository (e.g. `MyServerMods`).
3. Set visibility:
   - **Public:** Recommended for community modpacks. Players can sync without logging in.
   - **Private:** Requires all players to have at least Read access and log in with a PAT.
4. Initialize with a `main` branch.
5. In your `config.json`, set:
   ```json
   "repository": "https://github.com/<YOUR_USER>/<YOUR_REPO>.git",
   "branch": "main"
   ```

### Assigning Push Permissions
To give another admin push permissions:
1. Go to your repository on GitHub $\to$ **Settings** $\to$ **Collaborators**.
2. Click **Add people** and enter their GitHub username.
3. Choose role **Write** or **Admin**.
4. The collaborator accepts the invitation, logs in using `[4] GitHub Login` in their ModSync, and can now push mods.

---

## GitHub File Size Limits and Git LFS

- **Typical Minecraft Mods:** Almost all Minecraft mods range from $50\text{ KB}$ to $25\text{ MB}$. Normal Git handles these binary files efficiently.
- **GitHub Hard Limit:** GitHub rejects any single file exceeding **100 MB**.
- **GitHub Warning Limit:** GitHub warns on files exceeding **50 MB**.
- **ModSync Safety:** ModSync automatically inspects all `.jar` files before pushing. If any file exceeds `maxFileSizeMb` (100 MB), it halts the push and notifies the user with clear instructions, preventing repository corruption or rejected pushes.

---

## Troubleshooting & FAQ

### Minecraft is Running Warning
> *WARNING: Minecraft appears to currently be running.*
- Close Minecraft, CurseForge, Modrinth, or Java processes to avoid file lock errors (`IOException: The process cannot access the file`).

### Remote Repository Contains Newer Changes
> *Remote repository contains newer changes. Please sync first before pushing.*
- Another admin has pushed changes to GitHub.
- Run `[1] Sync Mods` first to update your local files, verify compatibility, and then push. ModSync never force-pushes.

### Authentication Failed
> *GitHub authentication failed. Your login or token may have expired.*
- Select `[4] GitHub Login` $\to$ Sign in with a new Personal Access Token.
- Verify that your token has the `repo` scope.

### Will ModSync Delete My Configs or Shaders?
- **No.** ModSync only manages files matching `allowedExtensions` (`.jar` by default). Subfolders, config files, shader packs, screenshots, and logs in the Minecraft folder are completely ignored and preserved.
