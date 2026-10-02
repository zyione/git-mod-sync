# ModSync

**ModSync** is a lightweight Windows application for synchronizing Minecraft mods, resource packs, and shaderpacks with independent GitHub repositories.

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
- **Zero-Terminal Experience:** Players simply click **Sync All**. No Git installation, command prompt, or complex setup required.
- **Self-Contained Single-File Executable:** Compiles to a single standalone `ModSync.exe` with bundled .NET runtime and automated portable Git provisioning (MinGit).
- **Safe Bidirectional Synchronization:**
  - **Sync (Download):** GitHub Remote $\to$ Internal Repository $\to$ `../mods`
  - **Push (Upload):** `../mods` $\to$ Internal Repository $\to$ GitHub Remote
- **Non-Destructive File Protection:** Only synchronizes specified mod files (`*.jar`). Never deletes unrelated files such as `README.txt`, configs, or `disabled-mods/`.
- **SHA-256 Checksums:** Compares mod files by cryptographic content hash rather than relying solely on filenames or timestamps.
- **Independent Modpack Repositories:** Sync mods, resource packs, and shaders from their own repositories. Sync All refreshes them concurrently, then validates and applies one combined plan. Existing root-JAR and single-repository layouts remain supported.
- **Atomic File Updates:** Copies incoming mods to `.tmp` files before renaming/replacing to prevent corrupted partial downloads. Includes automatic retry logic for locked files.
- **Active Minecraft Detection:** Warns users if Java/Minecraft or popular launchers (`javaw.exe`, `MinecraftLauncher.exe`) are currently open.
- **Automatic Application Updates:** In-app updater checks for new releases on GitHub, previews release notes and file sizes, streams downloads with live progress, and cleanly restarts ModSync via an atomic self-deleting batch process.
- **Personal Mod Exclusions & Ignore Rules:** Keep personal mods (e.g., mini-maps, shaders, client performance mods) without them being removed during Sync or uploaded during Push. Alternatively, ignore synced mods you don't want downloaded to your machine. Supports glob patterns (`*`, `?`), `.jar` name matching, and `.modignore` files.
- **Enterprise-Grade Security:** Tokens are stored exclusively in **Windows Credential Manager** (or DPAPI). Plaintext passwords and tokens are never written to `config.json` or logs.
- **Native Administrator Elevation:** Automatically prompts for Administrator privileges on launch via embedded Windows UAC application manifest, guaranteeing smooth file writes and seamless auto-updates.

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

### 1-Click Release Publisher (`release.bat`)

To deploy a new update for all players to receive automatically:
1. Double-click `release.bat` in the repository root (or run `.\scripts\release.ps1`).
2. Press **Enter** to accept the auto-incremented patch version (e.g., `1.0.1` $\to$ `1.0.2`), or type a custom version.
3. The script automatically:
   - Updates version metadata across project files.
   - Runs unit tests to ensure stability.
   - Compiles the self-contained `ModSync.exe`.
   - Creates a Git commit and annotated tag (e.g. `v1.0.2`).
   - Pushes commits and tags to GitHub.
4. **GitHub Actions** (`.github/workflows/release.yml`) automatically detects the tag, packages the release, and attaches `ModSync.exe` to GitHub Releases.
5. All connected ModSync clients immediately see the update banner on startup and can update in one click!

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
4. Click **Sync All**:
   - ModSync inspects your mods folder, compares cryptographic SHA-256 hashes against GitHub, and brings your folder in sync with the repository.
   - If confirmation is enabled, an Apple-inspired review sheet displays the exact added, updated, and removed files before applying.

### Fresh "Clean Reinstall" (For Messed-Up Folders)

Use **Clean Reinstall...** on the dashboard, or **Reinstall...** in Settings, to rebuild repository mods. Review the confirmation and click **Reinstall & Backup**.

1. ModSync snapshots the complete enabled resource pack and shader folders, including personal files, and Minecraft pack selection settings.
2. Existing non-excluded mods move into the backup's `mods/` folder. Excluded personal mods stay in place.
3. Repository mods are installed fresh. Shared packs follow normal synchronization, so personal packs stay on disk. Pack order and active shader follow enabled repository declarations. Worlds and other unrelated files stay untouched.

Backups live under the Minecraft instance's `modsync_backups/reinstall_<timestamp>_<unique ID>/`. The completion notification has an **Open Backup** action.

### Pack backups and restoration

Before an ordinary download replaces or removes an existing pack file, ModSync copies and verifies the old file and affected pack settings under `modsync_backups/sync_<timestamp>_<unique ID>/`. An already-current sync creates no backup. Newly added files have no previous version to save. If backup creation or verification fails, pack writes stop. Backups are retained until you delete them yourself.

Click **Backups** on the dashboard to open the backup folder. To restore, close Minecraft and copy desired files from `mods/`, `resourcepacks/`, or `shaderpacks/` into their original folders. `restore-paths.json` records the original locations of pack files and settings, including custom pack destinations. Restoring a backed-up `options.txt` restores its full contents. Re-running sync may reapply repository changes.

---

## Personal Mod Exclusions (Ignoring Mods)

ModSync features complete **bidirectional ignore support**, allowing players and admins to maintain personal mod customizations without interference.

### Use Cases:
1. **Personal Client Mods You Want to Keep:**
   - Keep client-only mods (such as Mini-maps, ReplayMod, custom HUDs, or shaders) in your local `mods/` directory.
   - When you click **Sync All**, ModSync will **never delete** these ignored mods.
   - If an admin clicks **Push Modpack**, ModSync will **never upload** your ignored personal mods to GitHub.
   - Even during a **Clean Reinstall**, personal ignored mods are safely preserved in place.

2. **Unwanted Synced Mods:**
   - If the repository contains a mod you don't want or can't run on your hardware (e.g., a heavy shader mod or high-res texture pack jar), add it to your ignore list.
   - When you click **Sync All**, ModSync will **skip downloading** it to your PC.
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
- ModSync preserves unrelated files, worlds, screenshots, and settings. When visual asset synchronization is enabled, it updates repository resource packs and shaderpacks. Personal packs remain on disk during downloads. Optional declarations change only the resource pack list and active shader settings.

## Independent Mods, Resource Packs, and Shader Repositories (v1.3.0)

The default sources are separate from the ModSync application source:

| Category | Repository | Branch |
|---|---|---|
| Mods and Fabric metadata | `zyione/4stoogies-mod-list` | `main` |
| Resource packs and their priority | `zyione/4stoogies-resourcepack-list` | `main` |
| Shaders and selected shader declaration | `zyione/4stoogies-shaderpack-list` | `main` |

Use **Repositories** on the dashboard to edit each URL and branch. The original `repository` and `branch` configuration keys still control mods. New keys are `resourcePackRepository`, `resourcePackBranch`, `shaderPackRepository`, and `shaderPackBranch`. Older config files without these keys automatically get the dedicated default repositories. An explicitly blank pack repository URL uses the mods repository for that category, preserving the earlier single-repository setup.

Recommended layouts:

```text
4stoogies-mod-list/
├── mods/
│   └── example.jar
├── fabric-version.txt
└── minecraft-version.txt

4stoogies-resourcepack-list/
├── CustomUI.zip
├── BaseTextures/
│   ├── pack.mcmeta
│   └── assets/
└── resourcepack-order.txt       # Optional; highest priority first

4stoogies-shaderpack-list/
├── Complementary.zip
└── active-shader.txt            # Optional; one existing shader ZIP filename
```

Dedicated pack repositories support packs at the root or in a `resourcepacks/` / `shaderpacks/` subfolder. If the named subfolder exists, it is authoritative. The order and active-shader declarations always live at the root of their respective repositories. Mods continue to use `mods/`, with legacy root JARs supported.

**Sync** or **Push** beside Resource Packs contacts only its repository. Shaders work the same way. New mods do not block a pack upload; newer changes in the selected pack repository still require syncing that category first. The upload preview names the exact selected files. Resource pack push includes the saved order automatically; shader selection remains administrator-managed `active-shader.txt` metadata.

**Sync All** downloads all enabled repositories concurrently using separate caches. Every download and declaration is validated before Minecraft file writes begin. Asset writes, settings, and the shared pack-ownership manifest are applied together in sequence to avoid concurrent settings/manifest writes. If any repository cannot refresh, Minecraft files remain untouched. Matching files remain unchanged.

**Push Modpack** preflights all enabled repositories, then commits and uploads changed categories independently. Multiple repository uploads are not a transaction: if a later upload fails, the error names categories already uploaded. A failed dedicated-pack upload can be retried from that category's Push button without syncing mods, even if there are no new file differences.

The mods cache keeps the original `repositoryFolder` path. Separate pack caches are its sibling paths with `-resourcepacks` and `-shaderpacks` suffixes. Caches, local asset folders, and backups must remain separate; linked cache paths are rejected. Switching a repository preserves existing Minecraft assets until an explicit sync.

These configuration keys apply to the local Minecraft instance:

| Key | Default | Meaning |
|---|---|---|
| `resourcePacksFolder` | `"resourcepacks"` | Local destination, relative to the parent of the resolved mods folder; absolute paths also work |
| `shaderPacksFolder` | `"shaderpacks"` | Local shader destination, resolved the same way |
| `syncResourcePacks` | `true` | Include resource ZIPs and extracted folders with a root `pack.mcmeta` |
| `syncShaderPacks` | `true` | Include shader ZIPs |
| `enforcePackOrder` | `true` | Apply `resourcepack-order.txt` when present |
| `enforceActiveShader` | `true` | Apply `active-shader.txt` when present |
| `publishResourcePackOrder` | `false` | Include the enabled shared-pack order from Minecraft when pushing |

The switches are available in Settings. The dashboard shows local and repository pack counts from the internal clone and the selected shader. Sync the repository to refresh that clone; these counts are not a live remote lookup. Asset destinations must be separate from each other, the mods folder, and the internal clone. Linked asset files and directories are rejected.

Example `resourcepack-order.txt`:

```text
# Highest priority first, matching Minecraft's in-game display
CustomUI.zip
BaseTextures
```

ModSync writes the reverse order into `options.txt`: built-in packs (with `vanilla` first), declared repository packs from lowest to highest priority, then existing personal packs. Personal packs therefore retain priority above the shared stack. Only declared repository packs are activated; other repository packs are still downloaded. Blank lines and `#` comments are ignored. Duplicate, unsafe, or missing pack names stop synchronization before asset writes. Malformed existing `resourcePacks` JSON also stops synchronization before any pack changes; fix the saved selection in Minecraft before retrying.

Example `active-shader.txt`:

```text
Complementary.zip
```

ModSync sets `shaderPack` and `enableShaders=true` in `config/iris.properties`, preserving other properties. It uses an existing `optionsshaders.txt` as a legacy fallback when Iris is absent. Iris detection includes mods incoming from the repository. If no shader configuration exists, it creates Iris settings; the shader loader must still be installed as a mod. Shader names are escaped for Java properties. Disabling shader enforcement leaves shader settings unchanged. Omitting a declaration leaves its corresponding player settings unchanged.

Incoming assets and settings use temporary files followed by atomic replacement. This protects individual files; a whole sync is not a filesystem transaction. A failure can leave some assets updated, so retry after resolving the reported error. Settings and pack ownership tracking are applied only after asset copies/deletions succeed.

ModSync tracks downloaded pack files in `.modsync-managed-packs.json` in the Minecraft instance. Later downloads remove only previously managed pack files that have disappeared from a successfully refreshed repository asset source, preserving local-only personal packs and unrelated files. Push treats local supported packs as authoritative, including local-only ZIPs; review the preview before uploading personal packs. Size limits apply to added/updated visual files as well as mods. Large packs may require a separately configured Git LFS workflow; this feature does not add automatic Git LFS provisioning.

## Separate Sync Actions (v1.1.0)

The dashboard keeps **Sync All** as its primary action, with Sync buttons beside each category:

| Action | What it updates |
|---|---|
| **Mods** | Managed JARs and Fabric Loader when enabled |
| **Resource Packs** | Resource ZIPs/extracted packs and the optional shared pack order |
| **Shaders** | Shader ZIPs and the optional selected shader |
| **Sync All** | All enabled categories and Fabric Loader |

Each action refreshes only its selected repositories, compares content only for those categories, and leaves matching files untouched. Unselected categories and their declarations are skipped, even if they need updates. The confirmation preview uses the refreshed clone. Changes are checked again before applying because files can change while the preview is open. Matching files and matching settings are not rewritten. Dashboard count refreshes inspect filenames/metadata without hashing large packs. Actual synchronization uses SHA-256 and checks copied data before replacement; unreadable files abort the operation instead of being treated as absent.

Settings changed while files are being synchronized are preserved: ModSync stops rather than overwriting those newer preferences. A whole sync is still not a transaction; completed asset changes remain if a later operation fails, and a retry finishes the remaining work.

### Publishing Resource Pack Priority

1. Enable and arrange the packs in Minecraft, with highest priority at the top, then close Minecraft to save the selection.
2. Click **Push** beside Resource Packs to publish files and saved order in the same upload. New packs participate immediately. Review both files and `resourcepack-order.txt` before confirming.
3. Other players click **Sync** beside Resource Packs or **Sync All** to download packs and apply their order together.
4. Use **Push Order** or **Sync Order** when only priority should change. Push Order includes only packs already shared and installed locally; it does not upload personal files.

Combined Resource Pack Push makes every uploaded pack shared, so newly uploaded selected packs enter the order. Disabled packs are uploaded but omitted from the enabled selection. Built-ins are excluded. An explicitly empty selection publishes an empty shared order; missing saved selection preserves the existing declaration. Personal selections on downloading players remain enabled and can override the shared stack. The former publishing/order-enforcement toggles are no longer required or shown.

## Verified Portable Updates and Delta Downloads (v1.1.0)

ModSync remains a self-contained portable `ModSync.exe`; existing instance paths and configurations continue to work. The updater now:

- Selects the exact `ModSync.exe` asset and verifies its GitHub SHA-256 digest, declared size, and embedded version.
- Resumes interrupted downloads when the server supports byte ranges, restarting cleanly when it does not. Partial data is retained for retry; invalid data is discarded. **Pause Download** preserves progress.
- Throttles progress reporting, retries temporary download failures, and detects stalled reads.
- Respects the automatic update setting. Background checks use a 15-minute metadata cache; manual checks contact GitHub immediately, with ETag conditional requests.
- Uses `ModSync-from-v<installed-version>.delta` only when it is smaller than the full EXE. The versioned patch format reuses content-defined chunks of the installed EXE and compresses new content with Brotli. Base checksum, patch checksum, bounded ranges, reconstructed checksum, size, and executable version are verified.
- Falls back to the full EXE when a patch is missing, incompatible, corrupt, or unusable. Exact verified downloads can be reused from `.updates/`.
- Waits for ModSync to close, then uses Windows file replacement with `ModSync.exe.previous` as a backup. Replacement or launch failures are recorded, with restoration attempted after replacement. The next launch reports the result. This is recovery from replacement/launch failure, not automatic detection of every possible later application crash.

These checks detect corrupted downloads and incorrect release assets. They do not substitute for Authenticode signing or protect against an attacker who can change the trusted repository's releases.

**Migration:** v1.0.12 and older clients need one full download to install v1.1.0, because their updater cannot apply patches. Clients with v1.1.0 or later can use delta downloads in subsequent releases. Skipped versions outside the three retained patch bases receive a full download. Savings depend on the changed code and bundled runtime; runtime changes can make a full download preferable.

GitHub Actions caches dependency downloads, tests the app, publishes the EXE, generates patches from up to three previous stable releases, verifies exact reconstruction, and uploads only patches smaller than the full EXE. `SHA256SUMS.txt` is published for manual verification. The full EXE remains available for manual installation and older clients. A failed reconstruction stops publication. Manual workflow runs require an existing version tag; release tags are never recreated by the local publisher.


## Pack Reinstall and Order Sync (v1.4.0)

Use **Reinstall…** beside Resource Packs or Shaders to refresh only that category's repository and replace every shared pack with a fresh, verified copy, including matching files. Before replacing anything, ModSync snapshots the selected pack folder (including personal files), its selection settings, and tracking metadata into `modsync_backups/`. Obsolete managed files are removed; local-only personal packs remain. Other categories and worlds stay untouched. Settings' Clean Reinstall action reinstalls only mods; the main Modpack reinstall retains its existing combined behavior.

Use **Sync Order** beside Resource Packs to apply `resourcepack-order.txt` without replacing installed pack files. The declaration lists highest priority first; Minecraft's stored list is reversed, with built-in and personal selections preserved. Required shared packs must already be installed; otherwise ModSync asks you to Sync Resource Packs first and leaves options unchanged. A missing declaration is reported rather than clearing your selection. Unchanged order makes no file changes or new backup. Changed options are backed up before replacement.

These actions refresh their repository cache normally; Sync Order avoids copying or hashing pack contents in the Minecraft instance, but its repository refresh may still download new pack data. Backups can be opened from Settings or the reinstall success message and include `restore-paths.json` for manual recovery. If a later file operation fails, earlier completed replacements may remain; the snapshot is retained for recovery.


**Publish order separately:** Arrange and enable shared packs in Minecraft, with the highest priority at the top, then close Minecraft to save the selection. Click **Push Order** beside Resource Packs, review the change, and confirm. This action publishes only `resourcepack-order.txt`, it does not upload pack files. Upload new packs with the ordinary pack Push first. Built-in and local-only personal packs are excluded. Other players click **Sync Order** to apply the published selection independently of pack-file sync. Resource Packs sync, Sync All, and Resource Pack Reinstall automatically apply the order. Personal selections stay above shared packs, so personal packs may override them.

Push Order can refresh an outdated repository cache without changing your installed packs or saved selection. It refuses to include unfinished uploads from earlier operations; finish those uploads first.


## Instance Selection, Status, and Recovery (v1.5.1)

The first launch asks you to confirm a Minecraft instance before changing its files. The app folder is suggested when it contains Minecraft files. The picker detects the default Minecraft folder and common PrismLauncher, MultiMC, Modrinth, and CurseForge instance folders, plus custom game directories declared in official launcher profiles. Discovery is bounded and does not search your whole disk. Use **Browse…** for portable launchers or custom locations; select the game folder containing options.txt or saves, not the launcher folder. Selecting a mods folder through Browse uses its parent. Launch a new game instance once before selecting it.

The confirmed folder is stored in config.json and survives executable updates. Older installations with a completed sync or a custom mods path retain their existing selection. A missing saved folder prompts for a valid replacement. Use **Choose Instance** on the main screen or the folder control in Settings to change it. Counts, statuses, Fabric information, and backup shortcuts reset for the newly chosen instance; relative pack paths follow its root.

**Check for Updates** is now available on the main screen. Category counts remain separate from verification: matching numbers do not prove matching files. Each category shows Not checked, Checking, Changes available, Up to date (last check), Published (sync to verify), Failed, or Disabled. A successful sync/check verifies the selected categories; reopening the app starts at Not checked. Fabric retains its installed-versus-required status. Background count checks do not hash every large pack or claim synchronization.

Failures now offer an appropriate action: retry the relevant preview/confirmation, sign in again, choose an instance, sync the selected section before pushing, open backups, or view logs. Close Minecraft before retrying locked files. No recovery action silently performs a clean reinstall or deletes backups. A whole sync is still not a transaction; completed file changes may remain after a later failure, so retained backups and retries remain important.

**Mods controls:** Sync, Push, and Reinstall… now sit beside Mods. Mods Push uploads only JAR changes in the mods repository. Mods Reinstall backs up non-excluded mods before reinstalling repository mods; excluded mods, resource packs, shaders, and worlds remain untouched. The main Push Modpack action still publishes all enabled categories.
