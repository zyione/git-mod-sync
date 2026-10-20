# Discord push announcements

Every copy of the same ModSync build uses the shared Discord destination. Anyone
who pushes through that build announces automatically; players and administrators
do not need to configure webhooks on their own PCs. Settings shows whether the
build has announcements enabled. No Discord bot is required.

## Build setup

Copy `.env.example` to `.env` in the repository root and set
`MODSYNC_DISCORD_WEBHOOK_URL` to your Discord channel webhook URL. `.env` and
`.env.*` are gitignored, except for the blank `.env.example`. Do not commit a
filled example. The build reads only that key and embeds it in the executable;
it never embeds the entire `.env`, copies the file to the output, or writes the
URL into `config.json` or logs. You distribute only the built executable.

An environment variable with the same name takes precedence over `.env` during
builds. GitHub Actions release builds read repository secret
**MODSYNC_DISCORD_WEBHOOK_URL**. Add it under repository **Settings → Secrets and
variables → Actions → New repository secret**. The release workflow requires a
valid webhook rather than silently releasing a build with announcements disabled.
Local release builds can use `-p:RequireDiscordWebhook=true` for the same check.
Ordinary development builds without a configured URL have announcements disabled.

Changing or removing the configured destination requires rebuilding and sharing
the updated app. The embedded URL is recoverable from distributed executables;
gitignore keeps it out of source control, not secret from app recipients. Use a
webhook dedicated to ModSync. Delete or regenerate it in Discord to revoke an old
build's ability to post. Building or saving `.env` does not send a test message.

Each explicit push through ModSync produces at most one message, listing its
successfully uploaded changes under Mods, Resource packs, and Shader packs.
Two mods uploaded together appear in the same message. Separate pushes remain
separate messages; merely happening close together does not combine them.
Direct GitHub pushes, checks, downloads, and no-op pushes do not post.

Names and added/updated/removed labels come from the committed difference against
the remote branch, including pending commits uploaded on retry. Folder packs are
listed once per pack, and long batches show a shortened list with the remaining
count. Pack order and active shader changes are included. Mentions are disabled;
the message does not ping everyone or a role.

The announcement waits until ModSync's publication attempt finishes. After a
successful publication, mods get a reminder to sync before playing; packs and
shaders get optional “Want these changes?” instructions. Existing resource-pack
and shader-only pushes require the owner to use **Modpack actions → Publish
modpack version** afterward. Those announcements say publication is still needed,
rather than telling players to download an unavailable version. Publication alone
does not send a second announcement.

If part of a multi-repository push fails, the message lists only repositories
that finished uploading and explains that publication is still needed. An entirely
failed push posts nothing. A Discord outage never rolls back or fails a GitHub
upload: ModSync appends a warning to its result. Delivery has a ten-second timeout;
it is not retried automatically because an uncertain response could otherwise
produce duplicate announcements. Live delivery requires a valid embedded webhook.
