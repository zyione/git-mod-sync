using System.Text.RegularExpressions;

namespace ModSync.Models;

public sealed class PackManifest
{
    public int Schema { get; set; } = 1;
    public string Version { get; set; } = "1.1";
    public string Minecraft { get; set; } = "1.20.1";
    public string? Fabric { get; set; }
    public List<PackRepository> Repositories { get; set; } = new();
    public List<PackFile> Files { get; set; } = new();
    public List<string>? ResourcePackOrder { get; set; }
    public string? ActiveShader { get; set; }
}
public sealed record PackRepository(string Category, string Url, string Branch, string Commit);
public sealed record PackFile(string Category, string Path, string RepositoryPath, long Size, string Sha256, string? FabricId = null);

public static class PackVersion
{
    public static bool Valid(string version) => Regex.IsMatch(version, @"^[1-9][0-9]*\.[1-9][0-9]*$") &&
        version.Split('.').All(p => int.TryParse(p, out _));
    public static string Next(string? current)
    {
        if (current == null) return "1.1";
        if (!Valid(current)) throw new InvalidDataException("Invalid published pack version.");
        var parts = current.Split('.');
        return parts[0] + "." + checked(int.Parse(parts[1]) + 1);
    }
}
