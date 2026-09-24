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
- [Instructions for Admins](#instructions-for-admins)
- [Setting Up the GitHub Repository](#setting-up-the-github-repository)
- [GitHub File Size Limits and Git LFS](#github-file-size-limits-and-git-lfs)
- [Troubleshooting & FAQ](#troubleshooting--faq)

---

## Key Features

- **Zero-Terminal Experience:** Players run `ModSync.exe` and press `1` to sync. No Git installation, command prompt, or complex setup required.
- **Self-Contained Single-File Executable:** Compiles to a single standalone `ModSync.exe` with bundled .NET runtime and automated portable Git provisioning (MinGit).
- **Safe Bidirectional Synchronization:**
  - **Sync (Download):** GitHub Remote $\to$ Internal Repository $\to$ `../mods`
  - **Push (Upload):** `../mods` $\to$ Internal Repository $\to$ GitHub Remote
- **Non-Destructive File Protection:** Only synchronizes specified mod files (`*.jar`). Never deletes unrelated files such as `README.txt`, configs, or `disabled-mods/`.
- **SHA-256 Checksums:** Compares mod files by cryptographic content hash rather than relying solely on filenames or timestamps.
- **Atomic File Updates:** Copies incoming mods to `.tmp` files before renaming/replacing to prevent corrupted partial downloads. Includes automatic retry logic for locked files.
- **Active Minecraft Detection:** Warns users if Java/Minecraft or popular launchers (`javaw.exe`, `MinecraftLauncher.exe`) are currently open.
- **Push Protection:** Never allows `--force` pushing. Aborts cleanly if remote changes have occurred since the last sync.
- **Enterprise-Grade Security:** Tokens are stored exclusively in **Windows Credential Manager** (or DPAPI). Plaintext passwords and tokens are never written to `config.json` or logs.

---

## Directory Layout

The application is deployed directly beside your Minecraft `mods` folder:

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
    ├── ModSync.exe              <-- Single executable
    ├── config.json              <-- Configuration file
    │
    ├── repository/              <-- Automatically created internal clone
    │   ├── .git/
    │   ├── fabric-api-0.92.0+1.20.1.jar
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
        ├── ModSync.csproj               # .NET 8 Project file
        ├── Program.cs                   # Interactive console UI and workflow runner
        │
        ├── Models/
        │   ├── AppConfig.cs             # Configuration schema
        │   ├── ModFileItem.cs           # Mod file metadata (path, size, SHA-256)
        │   ├── ModChange.cs             # Change item (Added, Removed, Updated)
        │   ├── SyncSummary.cs           # Aggregated change report
        │   └── GitStatusInfo.cs         # Repository status model
        │
        ├── Services/
        │   ├── ConfigService.cs         # Config loading, auto-generation, validation
        │   ├── LoggingService.cs        # Thread-safe sanitized daily file logger
        │   ├── MinecraftCheckService.cs # Best-effort running Minecraft detector
        │   ├── IGitService.cs           # Git operation abstraction
        │   ├── GitService.cs            # Git engine with MinGit auto-fallback
        │   ├── AuthenticationService.cs # Token validation and Windows Credential Manager
        │   └── ModSyncService.cs        # Core synchronization and atomic file engine
        │
        └── Utils/
            ├── HashUtils.cs             # Streaming SHA-256 calculations
            ├── PathUtils.cs             # BaseDirectory-relative path resolution
            ├── CredentialUtils.cs       # Windows Credential Manager (Advapi32) + DPAPI
            └── ConsoleUI.cs             # Formatted terminal UI and prompts
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
  "modsFolder": "../mods",
  "repositoryFolder": "./repository",
  "requireConfirmationBeforePush": true,
  "requireConfirmationBeforeSync": true,
  "allowedExtensions": [
    ".jar"
  ],
  "syncSubdirectories": false,
  "warnFileSizeMb": 50,
  "maxFileSizeMb": 100
}
```

### Options Description

| Key | Type | Default | Description |
|---|---|---|---|
| `repository` | string | `"https://github.com/zyione/4stoogies-mod-list.git"` | HTTPS Git clone URL of the mod repository |
| `savedRepositories` | array | `["..."]` | List of previously used / saved repository URLs for easy switching |
| `branch` | string | `"main"` | Target Git branch to track |
| `modsFolder` | string | `"../mods"` | Path to the Minecraft `mods` directory (relative to `ModSync.exe`) |
| `repositoryFolder` | string | `"./repository"` | Path to internal Git clone directory |
| `requireConfirmationBeforeSync` | bool | `true` | When `true`, displays update summary and prompts `[Y/N]` before applying |
| `requireConfirmationBeforePush` | bool | `true` | When `true`, displays changes and prompts `[Y/N]` before pushing to GitHub |
| `allowedExtensions` | array | `[".jar"]` | File extensions managed by ModSync. Non-matching files are never touched |
| `syncSubdirectories` | bool | `false` | When `false`, only checks top-level `.jar` files in `mods/` |
| `warnFileSizeMb` | integer | `50` | Displays a warning if any mod exceeds this size |
| `maxFileSizeMb` | integer | `100` | Aborts push if any mod exceeds GitHub's hard file limit |

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
4. Press `1` to **Sync Mods**.
5. ModSync will inspect your mods, show any added/updated/removed files, and bring your folder in sync with the server.

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
