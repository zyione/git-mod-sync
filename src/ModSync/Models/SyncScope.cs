namespace ModSync.Models;

[Flags]
public enum SyncScope
{
    None = 0,
    Mods = 1,
    ResourcePacks = 2,
    Shaders = 4,
    All = Mods | ResourcePacks | Shaders
}
