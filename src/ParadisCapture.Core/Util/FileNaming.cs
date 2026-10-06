using System.Text;
using System.Text.RegularExpressions;

namespace ParadisCapture.Core.Util;

public static partial class FileNaming
{
    public const string DefaultPrefix = "Recording";
    public const string InProgressSuffix = ".recording.mp4";
    private const int MaxPrefixLength = 40;

    /// <summary>
    /// "Recording_2026-10-05_10-42-31.mp4", or "Unity_2026-10-05_10-42-31.mp4" when an application
    /// name can be derived from the window title.
    /// </summary>
    public static string BuildFileName(string? windowTitle, DateTime timestamp)
    {
        string prefix = AppNameFromTitle(windowTitle) ?? DefaultPrefix;
        return $"{prefix}_{timestamp:yyyy-MM-dd_HH-mm-ss}.mp4";
    }

    /// <summary>
    /// Window titles usually end with the application name ("Notes.txt - Notepad",
    /// "Lecture | Microsoft Teams", "MyGame - SampleScene - Unity 6 (6000.0.1f1) &lt;DX11&gt;").
    /// Takes the last segment, removes bracketed noise and sanitizes it for a file name.
    /// </summary>
    public static string? AppNameFromTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        string[] parts = SeparatorRegex().Split(title);
        string candidate = parts.LastOrDefault(p => !string.IsNullOrWhiteSpace(p))?.Trim() ?? title;
        candidate = BracketRegex().Replace(candidate, " ");
        candidate = VersionRegex().Replace(candidate, " ");

        string sanitized = Sanitize(candidate);
        if (sanitized.Length == 0)
        {
            sanitized = Sanitize(BracketRegex().Replace(title, " "));
        }
        if (sanitized.Length == 0) return null;
        if (sanitized.Length > MaxPrefixLength) sanitized = sanitized[..MaxPrefixLength].TrimEnd('-', '.', ' ');
        return sanitized.Length == 0 ? null : sanitized;
    }

    /// <summary>Removes characters Windows does not allow in file names; spaces become '-'.</summary>
    public static string Sanitize(string value)
    {
        var invalid = new HashSet<char>("<>:\"/\\|?*");
        var sb = new StringBuilder(value.Length);
        bool lastDash = false;
        foreach (char ch in value.Trim())
        {
            if (char.IsControl(ch) || invalid.Contains(ch)) continue;
            if (char.IsWhiteSpace(ch) || ch == '_')
            {
                if (!lastDash && sb.Length > 0) { sb.Append('-'); lastDash = true; }
                continue;
            }
            sb.Append(ch);
            lastDash = false;
        }
        string s = sb.ToString().Trim('-', '.', ' ');
        // Reserved device names.
        string upper = s.ToUpperInvariant();
        if (upper is "CON" or "PRN" or "AUX" or "NUL" || ReservedRegex().IsMatch(upper)) s = "_" + s;
        return s;
    }

    /// <summary>
    /// Returns a path in <paramref name="folder"/> that does not exist yet (and whose in-progress
    /// companion does not exist either), appending " (2)", " (3)", ... when needed.
    /// </summary>
    public static string MakeUnique(string folder, string fileName, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        string baseName = Path.GetFileNameWithoutExtension(fileName);
        string ext = Path.GetExtension(fileName);
        for (int i = 1; ; i++)
        {
            string name = i == 1 ? baseName : $"{baseName} ({i})";
            string candidate = Path.Combine(folder, name + ext);
            if (!exists(candidate) && !exists(InProgressPathFor(candidate))) return candidate;
        }
    }

    /// <summary>"X.mp4" → "X.recording.mp4": the file written while recording.</summary>
    public static string InProgressPathFor(string finalPath)
    {
        string dir = Path.GetDirectoryName(finalPath) ?? "";
        return Path.Combine(dir, Path.GetFileNameWithoutExtension(finalPath) + InProgressSuffix);
    }

    /// <summary>"X.recording.mp4" → "X.mp4".</summary>
    public static string FinalPathFor(string inProgressPath)
    {
        string dir = Path.GetDirectoryName(inProgressPath) ?? "";
        string name = Path.GetFileName(inProgressPath);
        if (name.EndsWith(InProgressSuffix, StringComparison.OrdinalIgnoreCase))
            name = name[..^InProgressSuffix.Length] + ".mp4";
        return Path.Combine(dir, name);
    }

    [GeneratedRegex(@"\s+[-–—|]\s+")]
    private static partial Regex SeparatorRegex();

    [GeneratedRegex(@"[\(\[<{][^\)\]>}]*[\)\]>}]")]
    private static partial Regex BracketRegex();

    [GeneratedRegex(@"\b\d+(\.\d+){1,}\w*\b")]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"^(COM|LPT)[0-9]$")]
    private static partial Regex ReservedRegex();
}
