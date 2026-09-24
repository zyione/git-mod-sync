using ModSync.Models;

namespace ModSync.Utils;

/// <summary>
/// Console user interface helper providing colored text, banners, menus, and prompts.
/// </summary>
public static class ConsoleUI
{
    public const string AppVersion = "v1.0.0";

    public static void SafeClear()
    {
        try
        {
            if (!Console.IsOutputRedirected && !Console.IsInputRedirected)
            {
                Console.Clear();
            }
        }
        catch { }
    }

    public static void PrintHeader(string title)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("========================================");
        Console.WriteLine(CenterText(title, 40));
        Console.WriteLine("========================================");
        Console.ResetColor();
    }

    public static void PrintBanner(string repository, string branch, string modsFolder)
    {
        SafeClear();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("========================================");
        Console.WriteLine(CenterText($"MODSYNC {AppVersion}", 40));
        Console.WriteLine(CenterText("Minecraft Mod Manager", 40));
        Console.WriteLine("========================================");
        Console.ResetColor();
        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write("Repository:  ");
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine(ShortenUrl(repository));

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write("Branch:      ");
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine(branch);

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write("Mods Folder: ");
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine(modsFolder);
        Console.ResetColor();
        Console.WriteLine();
    }

    public static void PrintMenu()
    {
        Console.WriteLine("[1] Sync Mods");
        Console.WriteLine("[2] Push Mods");
        Console.WriteLine("[3] Check Status");
        Console.WriteLine("[4] GitHub Login");
        Console.WriteLine("[5] Switch / Change Repository");
        Console.WriteLine("[6] Settings / Config");
        Console.WriteLine("[0] Exit");
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write("Select: ");
        Console.ResetColor();
    }

    public static void PrintSuccess(string message)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(message);
        Console.ResetColor();
    }

    public static void PrintWarning(string message)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(message);
        Console.ResetColor();
    }

    public static void PrintError(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(message);
        Console.ResetColor();
    }

    public static void PrintInfo(string message)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(message);
        Console.ResetColor();
    }

    public static void PrintDiff(ModChange change)
    {
        switch (change.Type)
        {
            case ChangeType.Added:
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"+ {change.RelativePath}");
                break;
            case ChangeType.Removed:
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"- {change.RelativePath}");
                break;
            case ChangeType.Updated:
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"~ {change.RelativePath}");
                break;
        }
        Console.ResetColor();
    }

    public static void PrintChangesSummary(SyncSummary summary, string actionTitle = "Checking for updates...")
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(actionTitle);
        Console.ResetColor();
        Console.WriteLine();

        if (summary.AddedCount > 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("Added:");
            foreach (var item in summary.Added)
                Console.WriteLine($"  + {item.RelativePath}");
            Console.WriteLine();
        }

        if (summary.RemovedCount > 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Removed:");
            foreach (var item in summary.Removed)
                Console.WriteLine($"  - {item.RelativePath}");
            Console.WriteLine();
        }

        if (summary.UpdatedCount > 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("Updated:");
            foreach (var item in summary.Updated)
                Console.WriteLine($"  ~ {item.RelativePath}");
            Console.WriteLine();
        }

        Console.ResetColor();
    }

    public static bool Confirm(string question, bool defaultYes = true)
    {
        string prompt = defaultYes ? "[Y/n]" : "[y/N]";
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write($"{question} {prompt}: ");
        Console.ResetColor();

        string? input = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(input))
            return defaultYes;

        return input == "y" || input == "yes";
    }

    public static void Pause(string message = "Press ENTER to continue...")
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(message);
        Console.ResetColor();
        Console.ReadLine();
    }

    public static string ReadPassword(string prompt)
    {
        Console.Write(prompt);

        if (Console.IsInputRedirected)
        {
            return Console.ReadLine() ?? string.Empty;
        }

        var password = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                break;
            }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (password.Length > 0)
                {
                    password.Remove(password.Length - 1, 1);
                    Console.Write("\b \b");
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                password.Append(key.KeyChar);
                Console.Write("*");
            }
        }
        return password.ToString();
    }

    private static string CenterText(string text, int width)
    {
        if (text.Length >= width) return text;
        int leftPadding = (width - text.Length) / 2;
        return text.PadLeft(leftPadding + text.Length).PadRight(width);
    }

    private static string ShortenUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return "(not configured)";
        if (url.Length <= 45) return url;
        return url[..42] + "...";
    }
}
