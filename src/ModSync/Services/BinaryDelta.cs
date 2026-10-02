using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace ModSync.Services;

/// <summary>Versioned, bounded content-defined binary patches for portable executables.</summary>
public static class BinaryDelta
{
    public const long MaxOutputBytes = 256L * 1024 * 1024;
    private const int MinChunk = 4096, MaxChunk = 65536;
    private static readonly ulong[] Gear = Enumerable.Range(0, 256).Select(i =>
    {
        ulong x = (ulong)i + 0x9e3779b97f4a7c15UL;
        x = (x ^ (x >> 30)) * 0xbf58476d1ce4e5b9UL;
        x = (x ^ (x >> 27)) * 0x94d049bb133111ebUL;
        return x ^ (x >> 31);
    }).ToArray();

    public static string Hash(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static IEnumerable<(int Offset, int Length)> Chunks(byte[] bytes)
    {
        int start = 0;
        ulong rolling = 0;
        for (int i = 0; i < bytes.Length; i++)
        {
            rolling = (rolling << 1) + Gear[bytes[i]];
            int length = i - start + 1;
            if (length >= MaxChunk || (length >= MinChunk && (rolling & 16383) == 0))
            {
                yield return (start, length);
                start = i + 1;
                rolling = 0;
            }
        }
        if (start < bytes.Length) yield return (start, bytes.Length - start);
    }

    public static void Create(string original, string updated, string patch)
    {
        if (new FileInfo(original).Length > MaxOutputBytes || new FileInfo(updated).Length > MaxOutputBytes)
            throw new InvalidDataException("Executable exceeds the supported patch size.");
        var oldBytes = File.ReadAllBytes(original);
        var newBytes = File.ReadAllBytes(updated);
        var lookup = new Dictionary<string, (int Offset, int Length)>();
        foreach (var chunk in Chunks(oldBytes))
            lookup.TryAdd(Convert.ToHexString(SHA256.HashData(oldBytes.AsSpan(chunk.Offset, chunk.Length))), chunk);
        using var output = File.Create(patch);
        using (var header = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            header.Write("MSD1"u8.ToArray());
            header.Write(SHA256.HashData(oldBytes));
            header.Write(SHA256.HashData(newBytes));
            header.Write((long)newBytes.Length);
        }
        using var compression = new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true);
        using var writer = new BinaryWriter(compression);
        foreach (var chunk in Chunks(newBytes))
        {
            var hash = Convert.ToHexString(SHA256.HashData(newBytes.AsSpan(chunk.Offset, chunk.Length)));
            if (lookup.TryGetValue(hash, out var match))
            {
                writer.Write((byte)0);
                writer.Write((long)match.Offset);
                writer.Write(match.Length);
            }
            else
            {
                writer.Write((byte)1);
                writer.Write(chunk.Length);
                writer.Write(newBytes, chunk.Offset, chunk.Length);
            }
        }
        writer.Write((byte)255);
    }

    public static void Apply(string original, string patch, string destination)
    {
        if (Path.GetFullPath(destination).Equals(Path.GetFullPath(original), StringComparison.OrdinalIgnoreCase) ||
            Path.GetFullPath(destination).Equals(Path.GetFullPath(patch), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Patch output must be a separate staging file.");
        using var basis = File.OpenRead(original);
        using var input = File.OpenRead(patch);
        using var header = new BinaryReader(input, System.Text.Encoding.UTF8, leaveOpen: true);
        if (!header.ReadBytes(4).AsSpan().SequenceEqual("MSD1"u8)) throw new InvalidDataException("Unsupported patch format.");
        var oldHash = header.ReadBytes(32);
        var newHash = header.ReadBytes(32);
        long expected = header.ReadInt64();
        if (expected < 1 || expected > MaxOutputBytes || newHash.Length != 32 ||
            !SHA256.HashData(basis).AsSpan().SequenceEqual(oldHash))
            throw new InvalidDataException("Patch does not match the installed executable.");
        using var decompression = new BrotliStream(input, CompressionMode.Decompress);
        using var reader = new BinaryReader(decompression);
        try
        {
            using (var output = File.Create(destination))
            {
                var buffer = new byte[MaxChunk];
                while (true)
                {
                    byte operation = reader.ReadByte();
                    if (operation == 255) break;
                    long offset = operation == 0 ? reader.ReadInt64() : 0;
                    int length = reader.ReadInt32();
                    if (operation > 1 || length < 1 || length > MaxChunk || output.Length + length > expected)
                        throw new InvalidDataException("Invalid patch operation.");
                    if (operation == 0)
                    {
                        if (offset < 0 || offset > basis.Length - length) throw new InvalidDataException("Invalid patch source range.");
                        basis.Position = offset;
                        basis.ReadExactly(buffer.AsSpan(0, length));
                    }
                    else decompression.ReadExactly(buffer.AsSpan(0, length));
                    output.Write(buffer, 0, length);
                }
                if (output.Length != expected || decompression.ReadByte() != -1)
                    throw new InvalidDataException("Incomplete or trailing patch data.");
            }
            if (!Convert.FromHexString(Hash(destination)).AsSpan().SequenceEqual(newHash))
                throw new InvalidDataException("Reconstructed executable checksum failed.");
        }
        catch
        {
            if (File.Exists(destination)) File.Delete(destination);
            throw;
        }
    }
}
