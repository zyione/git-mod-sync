# ModSync

**ModSync** is a lightweight Windows application for synchronizing Minecraft mods, resource packs, and shaderpacks with one authoritative GitHub repository.

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
- **Single Repository Modpacks:** Sync mods, resource packs (ZIPs or extracted folders), shader ZIPs, resource pack precedence, and an optional active shader in one pass. Existing repositories with root-level JARs remain supported.
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

## Resource Packs and Shaders in One Repository

Use this layout in the modpack repository (separate from the ModSync application source):

```text
my-modpack-repo/
├── fabric-version.txt
├── minecraft-version.txt
├── resourcepack-order.txt       # Optional: highest priority first
├── active-shader.txt            # Optional: one shader ZIP filename
├── mods/
│   └── example.jar
├── resourcepacks/
│   ├── CustomUI.zip
│   └── BaseTextures/
│       ├── pack.mcmeta
│       └── assets/
└── shaderpacks/
    └── Complementary.zip
```

Click **Sync All** to download all enabled asset types and apply the optional declarations. **Push Modpack** uploads local mods and enabled visual assets to their corresponding folders in the same repository, using one branch and one login. Resource pack order can be published explicitly from Minecraft with the Settings switch described below. Shader declarations remain administrator-managed repository metadata; pushing does not upload your personal Minecraft settings.

Root-level JAR repositories remain supported. If `mods/` exists, ModSync uses it as the authoritative mod source. Visual assets always use repository folders named `resourcepacks/` and `shaderpacks/`. Missing visual folders are skipped during downloads, so existing mod-only repositories leave player visuals untouched. Keep a folder present (for example with a README) when removing its last managed pack.

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

ModSync writes the reverse order into `options.txt`: built-in packs (with `vanilla` first), declared repository packs from lowest to highest priority, then existing personal packs. Personal packs therefore retain priority above the shared stack. Only declared repository packs are activated; other repository packs are still downloaded. Blank lines and `#` comments are ignored. Duplicate, unsafe, or missing pack names stop synchronization before asset writes. Malformed existing `resourcePacks` JSON also stops order enforcement; disable enforcement to keep that setting untouched.

Example `active-shader.txt`:

```text
Complementary.zip
```

ModSync sets `shaderPack` and `enableShaders=true` in `config/iris.properties`, preserving other properties. It uses an existing `optionsshaders.txt` as a legacy fallback when Iris is absent. Iris detection includes mods incoming from the repository. If no shader configuration exists, it creates Iris settings; the shader loader must still be installed as a mod. Shader names are escaped for Java properties. Disabling enforcement or omitting either declaration leaves its corresponding player settings unchanged.

Incoming assets and settings use temporary files followed by atomic replacement. This protects individual files; a whole sync is not a filesystem transaction. A failure can leave some assets updated, so retry after resolving the reported error. Settings and pack ownership tracking are applied only after asset copies/deletions succeed.

ModSync tracks downloaded pack files in `.modsync-managed-packs.json` in the Minecraft instance. Later downloads remove only previously managed pack files that have disappeared from an existing repository asset folder, preserving local-only personal packs and unrelated files. Push treats local supported packs as authoritative, including local-only ZIPs; review the preview before uploading personal packs. Size limits apply to added/updated visual files as well as mods. Large packs may require a separately configured Git LFS workflow; this feature does not add automatic Git LFS provisioning.

## Separate Sync Actions (v1.1.0)

The dashboard keeps **Sync All** as its primary action, with three smaller actions below it:

| Action | What it updates |
|---|---|
| **Mods** | Managed JARs and Fabric Loader when enabled |
| **Resource Packs** | Resource ZIPs/extracted packs and the optional shared pack order |
| **Shaders** | Shader ZIPs and the optional selected shader |
| **Sync All** | All enabled categories and Fabric Loader |

Each action refreshes the one shared repository, compares content only for the selected categories, and leaves matching files untouched. Unselected categories and their declarations are skipped, even if they need updates. The confirmation preview uses the refreshed clone. Changes are checked again before applying because files can change while the preview is open. Matching files and matching settings are not rewritten. Dashboard count refreshes inspect filenames/metadata without hashing large packs. Actual synchronization uses SHA-256 and checks copied data before replacement; unreadable files abort the operation instead of being treated as absent.

Settings changed while files are being synchronized are preserved: ModSync stops rather than overwriting those newer preferences. A whole sync is still not a transaction; completed asset changes remain if a later operation fails, and a retry finishes the remaining work.

### Publishing Resource Pack Priority

1. Upload new shared packs with **Push Modpack** first.
2. In Minecraft, enable the shared packs and arrange them in the desired order. The pack at the top has highest priority. Close Minecraft so its selection is saved.
3. In ModSync Settings, enable **Publish Resource Pack Order**.
4. Click **Push Modpack** and review `resourcepack-order.txt (shared pack priority)` in the changes.
5. Push. Other players apply the declaration with **Resource Packs** or **Sync All**, when order enforcement is enabled.

The published declaration includes only enabled packs already present in the repository and still available locally. Built-in packs and local-only personal packs never enter the declaration. Disabled shared packs are omitted. An empty published order disables the shared packs while preserving built-in and personal selections on players' machines. Leave the publishing switch off for ordinary player use. It controls the declaration only; review the separate file uploads when pushing.

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
