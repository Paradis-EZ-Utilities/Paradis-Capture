namespace ParadisCapture.Core.Video;

/// <summary>
/// Recording quality presets. Resolution never changes between presets: only how hard the video is
/// compressed, how often a keyframe is written and the audio bitrate.
/// <para>The numeric values are stable: settings files store the name, but code relies on the order.</para>
/// </summary>
public enum VideoQuality
{
    /// <summary>Lectures, slides, code, tutorials: mostly static screens. Smallest files.</summary>
    Compact = 0,
    /// <summary>General desktop recording. The default. Unchanged since 1.0.0.</summary>
    Standard = 1,
    /// <summary>Games, animation, fast scrolling: noticeably better motion than Standard.</summary>
    High = 2,
    /// <summary>Fidelity first. The original prototype's top quality level; largest files.</summary>
    Maximum = 3,
}

public static class EncoderSettings
{
    public const int MaxBitrate = 100_000_000;

    private const double Pixels1080p = 1920.0 * 1080.0;

    /// <summary>Mean video bitrate at 1080p30 for Compact and Standard.</summary>
    private const double Compact1080p30 = 900_000;
    private const double Standard1080p30 = 1_800_000;

    /// <summary>
    /// Bits per pixel per frame (at the 30 FPS reference) for the motion presets. These are the
    /// original Simple Recorder prototype's "Standard" (0.08) and "Very high" (0.18) levels.
    /// </summary>
    private const double HighBitsPerPixel = 0.08;
    private const double MaximumBitsPerPixel = 0.18;

    /// <summary>
    /// Picks the mean H.264 bitrate for a recording. The encoder runs in unconstrained VBR mode, so
    /// this is an average: a still slide costs far less, a burst of scrolling briefly costs more.
    /// <para>
    /// Compact and Standard scale with resolution sub-linearly (a 4K screen of text doesn't carry
    /// four times the information of a 1080p one): 1080p30 is ~0.9 / ~1.8 Mbps, a 1280×720 window
    /// ~0.5 / ~1.0 Mbps, 4K30 ~2.7 / ~5.5 Mbps.
    /// </para>
    /// <para>
    /// High and Maximum are for motion, where every pixel changes, so they scale linearly with the
    /// pixel count: 1080p30 ~5.0 / ~11.2 Mbps, 1080p60 ~8.4 / ~18.8 Mbps, 1280×720@30
    /// ~2.2 / ~5.0 Mbps, 4K60 ~34 / ~75 Mbps.
    /// </para>
    /// <para>
    /// Every preset weights frame rate as (fps / 30)^0.75, so 60 FPS gets ~1.7× the bandwidth of
    /// 30 FPS: more than 30, but less than double, because consecutive frames are more alike.
    /// </para>
    /// </summary>
    public static int BitrateFor(int width, int height, int fps, VideoQuality quality)
    {
        double pixels = Math.Max(1.0, (double)width * height);
        double fpsFactor = FrameRateFactor(fps);

        double bitrate = quality switch
        {
            VideoQuality.High => pixels * 30.0 * fpsFactor * HighBitsPerPixel,
            VideoQuality.Maximum => pixels * 30.0 * fpsFactor * MaximumBitsPerPixel,
            VideoQuality.Compact => Compact1080p30 * Math.Pow(pixels / Pixels1080p, 0.8) * fpsFactor,
            _ => Standard1080p30 * Math.Pow(pixels / Pixels1080p, 0.8) * fpsFactor,
        };
        return (int)Math.Clamp(bitrate, MinBitrateFor(quality), MaxBitrate);
    }

    /// <summary>How much more bandwidth a frame rate gets relative to 30 FPS.</summary>
    public static double FrameRateFactor(int fps) => Math.Pow(Math.Max(1, fps) / 30.0, 0.75);

    /// <summary>Floor so that small windows and regions still get a usable bitrate.</summary>
    public static int MinBitrateFor(VideoQuality quality) => quality switch
    {
        VideoQuality.Compact => 300_000,
        VideoQuality.Standard => 600_000,
        VideoQuality.High => 1_000_000,
        _ => 2_000_000,
    };

    /// <summary>AAC bitrate in bits per second. Media Foundation's AAC encoder accepts 96, 128, 160 and 192 kbps.</summary>
    public static int AudioBitrateFor(VideoQuality quality) => quality switch
    {
        VideoQuality.Compact => 96_000,
        VideoQuality.Standard => 128_000,
        _ => 192_000,
    };

    /// <summary>
    /// Keyframe interval. High and Maximum keep a keyframe every 2 seconds: precise seeking, and
    /// in fast motion a keyframe costs little more than the frames around it. Compact and Standard
    /// use 4 seconds: on a mostly still screen keyframes are a large share of the file, and
    /// seeking to within 4 seconds is fine for a lecture.
    /// </summary>
    public static int GopSizeFor(int fps, VideoQuality quality) =>
        Math.Max(1, fps * (quality >= VideoQuality.High ? 2 : 4));

    /// <summary>Rough file size per hour, for the hint in Settings (assumes the full mean bitrate is used).</summary>
    public static double GigabytesPerHour(int videoBitrate, int audioBitrate) =>
        (videoBitrate + (double)audioBitrate) / 8.0 * 3600 / 1_000_000_000.0;
}
