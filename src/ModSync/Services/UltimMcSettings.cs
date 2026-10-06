using System.Text;

namespace ModSync.Services;

/// <summary>UltimMC uses escaped flat key/value lines, not Windows INI sections.</summary>
public sealed class UltimMcSettings(string text)
{
    public string Text { get; } = text;
    public string? Get(string key) => Text.Split('\n').Select(Parse).LastOrDefault(p => p.Key == key).Value;
    private static (string? Key, string? Value) Parse(string line)
    {
        int comment = -1;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '\\') { i++; continue; }
            if (line[i] == '#') { comment = i; break; }
        }
        if (comment >= 0) line = line[..comment];
        int equal = line.IndexOf('=');
        return equal < 0 ? (null, null) : (line[..equal].Trim(), Decode(line[(equal + 1)..].Trim()));
    }
    public static string Decode(string value)
    {
        var result = new StringBuilder();
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\') result.Append(value[i]);
            else if (++i < value.Length) result.Append(value[i] switch { 'n' => '\n', 't' => '\t', _ => value[i] });
        }
        return result.ToString();
    }
    public static string Encode(string value) => value.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\t", "\\t").Replace("#", "\\#");
    public string With(IReadOnlyDictionary<string, string?> changes)
    {
        string newline = Text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = Text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => !changes.ContainsKey(Parse(l).Key ?? "")).ToList();
        while (lines.Count > 0 && lines[^1] == "") lines.RemoveAt(lines.Count - 1);
        lines.AddRange(changes.Where(p => p.Value != null).Select(p => p.Key + "=" + Encode(p.Value!)));
        return string.Join(newline, lines) + newline;
    }
}
