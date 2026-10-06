# Published modpack versions

ModSync's app version and the modpack version are separate. The pack starts at **1.1**, followed by **1.2**, **1.3**, **1.12**, **1.100**, and so on. These are integer release numbers, not decimal fractions. The major version never increases automatically. A future explicit major-version action must be reviewed separately.

## First publication

1. Open the new ModSync build and confirm the intended instance and repositories.
2. Review personal-mod exclusions. If you added no personal mods, use **Skip — no personal mods**. Push any intended shared content changes using the existing authoring workflow.
3. Choose **Modpack actions → Publish modpack version**. Sign in if requested, then choose the action again.
4. Approve preparing the version. This authoring step may download repositories to calculate file checksums. It reads local game files only through the existing authoring workflow; publication itself does not change the instance.
5. Review the version, file count, and repository count. Choose **Publish 1.1** to upload `modsync-manifest.json` to the mods repository's configured branch.

No initial version is published automatically by installing the app. Until publication, launch checks explain that no manifest exists and offer retry, explicit Play anyway, or cancellation. Checks never silently clone repositories as a fallback.

The manifest records exact commits from the mods, resource-pack, and shader repositories, plus Minecraft/Fabric versions and shared pack/shader selections. A later repository commit does not change an already published snapshot. Publish another pack version when that content should reach players. Fabric checking requires a published loader version, obtained from `fabric-version.txt` or the author's configured target.

## Checking and installing

- **Check Now / UltimMC launch:** download only the small manifest, then compare installed files. No JARs, ZIPs, or Git history are downloaded while checking.
- **First check:** calculate local checksums. Later checks reuse a saved checksum only when the filename, file size, modification time, and expected checksum all match.
- **Changed expectation:** a new checksum in the published manifest forces a fresh local checksum, even if the filename and local metadata are unchanged.
- **Review:** show the installed and available versions, additions/replacements/removals, exclusions, and estimated download size.
- **Approval:** download only required files at the pinned commits. Reuse checksum-verified files in `ModSync/content`. Public files use GitHub's raw endpoint; authenticated repositories use its contents API.
- **Verification:** verify size and SHA-256 before installation. Recheck the approved local snapshot, back up replaced/removed files, and apply changes. Full enabled-content verification and loader checks must pass before recording the version as installed.
- **Completion:** ask whether to close ModSync; during pre-launch, both completion choices continue Minecraft.

The manifest is capped at 4 MiB and 20,000 files. This version supports ordinary GitHub files up to 100 MiB each; Git LFS assets and external download hosts are not supported. Publishing such content requires a different distribution backend.

## Personal files and recovery

The “Have you excluded your personal mods?” reminder remains on startup/launch and returns when new unexcluded local JAR names appear. Filename, wildcard, and Fabric-ID exclusions apply before downloads. Files that ModSync has never managed are kept, including personal additions absent from the manifest. An initial baseline therefore does not remove unknown old versions automatically; review those files and exclusions yourself.

**Full verification / Repair** ignores the metadata cache and hashes installed managed files. Ordinary fast checks can miss unusual edits preserving file size and modification time; full verification detects those. The app displays the last verified pack version, not a guarantee of server compatibility.

Checks and downloads can be cancelled without changing installed files. Completed verified downloads are retained for retries; an interrupted individual download starts again. Cancellation is temporarily unavailable while verifying/applying the approved installation.

An update journal and ownership state live outside the instance in `ModSync/pack-state`. An interrupted installation clears its verified version, retains backups and file ownership, and requires another check. Rechecking computes a fresh plan and can finish the update. This is recoverable installation, not an atomic multi-file transaction or automatic rollback: some changes can remain after an installation failure. Do not launch a partially updated pack until verification succeeds.

Settings and caches remain under `UltimMC/ModSync`. All development tests use disposable instances; the developer does not write directly into player instances.
