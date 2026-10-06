namespace ParadisCapture.Core.Video;

/// <summary>
/// Recording quality presets. Resolution never changes between presets: only how hard the video is
/// compressed, how often a keyframe is written and the audio bitrate.
/// </summary>
public enum VideoQuality
{
    /// <summary>Lectures, slides, code, tutorials: mostly static screens. Smallest files.</summary>
    Compact = 0,
    /// <summary>General desktop recording. The default.</summary>
    Standard = 1,
    /// <summary>Games, animation, fast motion. The original (pre-1.0) quality level.</summary>
    High = 2,
}

public static class EncoderSettings
{
    public const int MaxBitrate = 100_000_000;

    private const double Pixels1080p = 1920.0 * 1080.0;

    /// <summary>Mean video bitrate at 1080p30 for Compact and Standard.</summary>
    private const double Compact1080p30 = 900_000;
    private const double Standard1080p30 = 1_800_000;

    /// <summary>
    /// Picks the mean H.264 bitrate for a recording. The encoder runs in unconstrained VBR mode, so
    /// this is an average: a still slide costs far less, a burst of scrolling briefly costs more.
    /// <para>
    /// Compact and Standard scale with resolution sub-linearly (a 4K screen of text doesn't carry
    /// four times the information of a 1080p one): 1080p30 is ~0.9 / ~1.8 Mbps, a 1280×720 window
    /// ~0.5 / ~1.0 Mbps, 4K30 ~2.7 / ~5.5 Mbps. High keeps the original formula (1080p30 ~7.5 Mbps,
    /// 1080p60 ~12.6 Mbps). 60 FPS costs more than 30, but well under double, because consecutive
    /// frames are more alike.
    /// </para>
    /// </summary>
    public static int BitrateFor(int width, int height, int fps, VideoQuality quality)
    {
        double pixels = Math.Max(1.0, (double)width * height);
        double fpsFactor = Math.Pow(Math.Max(1, fps) / 30.0, 0.75);

        if (quality == VideoQuality.High)
        {
            double bitrate = pixels * 30.0 * fpsFactor * 0.12;
            return (int)Math.Clamp(bitrate, MinBitrateFor(quality), MaxBitrate);
        }

        double reference = quality == VideoQuality.Compact ? Compact1080p30 : Standard1080p30;
        double scaled = reference * Math.Pow(pixels / Pixels1080p, 0.8) * fpsFactor;
        return (int)Math.Clamp(scaled, MinBitrateFor(quality), MaxBitrate);
    }

    /// <summary>Floor so that small windows and regions still get a usable bitrate.</summary>
    public static int MinBitrateFor(VideoQuality quality) => quality switch
    {
        VideoQuality.Compact => 300_000,
        VideoQuality.Standard => 600_000,
        _ => 1_500_000,
    };

    /// <summary>AAC bitrate in bits per second. Media Foundation's AAC encoder accepts 96, 128, 160 and 192 kbps.</summary>
    public static int AudioBitrateFor(VideoQuality quality) => quality switch
    {
        VideoQuality.Compact => 96_000,
        VideoQuality.Standard => 128_000,
        _ => 192_000,
    };

    /// <summary>
    /// Keyframe interval. High keeps a keyframe every 2 seconds for precise seeking. Compact and
    /// Standard use 4 seconds: on a mostly still screen keyframes are a large share of the file, and
    /// seeking to within 4 seconds is fine for a lecture.
    /// </summary>
    public static int GopSizeFor(int fps, VideoQuality quality) =>
        Math.Max(1, fps * (quality == VideoQuality.High ? 2 : 4));

    /// <summary>Rough file size per hour, for the hint in Settings (assumes the full mean bitrate is used).</summary>
    public static double GigabytesPerHour(int videoBitrate, int audioBitrate) =>
        (videoBitrate + (double)audioBitrate) / 8.0 * 3600 / 1_000_000_000.0;
}
