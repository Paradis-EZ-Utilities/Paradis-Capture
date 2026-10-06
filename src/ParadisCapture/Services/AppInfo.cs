using System.Reflection;

namespace ParadisCapture.Services;

/// <summary>Product name and version, read from the assembly metadata set in the project files.</summary>
public static class AppInfo
{
    public const string Name = "Paradis Capture";
    public const string Tagline = "Simple Screen & Audio Recorder";

    /// <summary>"1.0.0" — the informational version, without any build suffix.</summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var asm = typeof(AppInfo).Assembly;
        string? v = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(v)) return asm.GetName().Version?.ToString(3) ?? "1.0.0";
        int plus = v.IndexOf('+');
        return plus >= 0 ? v[..plus] : v;
    }
}
