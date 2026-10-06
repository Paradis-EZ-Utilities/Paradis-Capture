using System.Buffers.Binary;
using ParadisCapture.Core.Audio;
using ParadisCapture.Core.Util;
using ParadisCapture.Core.Video;

namespace ParadisCapture.Core.Tests;

public class SampleConverterTests
{
    [Fact]
    public void MonoIsDuplicated()
    {
        var conv = new SampleConverter(new CaptureFormat(48000, 1, 16, false));
        var src = new byte[4];
        BinaryPrimitives.WriteInt16LittleEndian(src, 16384);
        BinaryPrimitives.WriteInt16LittleEndian(src.AsSpan(2), -16384);
        var dst = new float[4];
        conv.Convert(src, 2, dst);
        Assert.Equal(new[] { 0.5f, 0.5f, -0.5f, -0.5f }, dst);
    }

    [Fact]
    public void StereoFloatPassesThrough()
    {
        var conv = new SampleConverter(new CaptureFormat(48000, 2, 32, true, 0x3));
        var src = new byte[8];
        BinaryPrimitives.WriteSingleLittleEndian(src, 0.25f);
        BinaryPrimitives.WriteSingleLittleEndian(src.AsSpan(4), -0.75f);
        var dst = new float[2];
        conv.Convert(src, 1, dst);
        Assert.Equal(new[] { 0.25f, -0.75f }, dst);
    }

    [Fact]
    public void FivePointOneDownmixDropsLfeAndFoldsCenter()
    {
        // FL FR FC LFE BL BR
        var conv = new SampleConverter(new CaptureFormat(48000, 6, 32, true, 0x3F));
        float[] ch = { 0.1f, 0.2f, 0.4f, 1.0f, 0.3f, 0f };
        var src = new byte[24];
        for (int i = 0; i < 6; i++) BinaryPrimitives.WriteSingleLittleEndian(src.AsSpan(i * 4), ch[i]);
        var dst = new float[2];
        conv.Convert(src, 1, dst);
        Assert.Equal(0.1f + 0.4f * 0.70710678f + 0.3f * 0.70710678f, dst[0], 4);
        Assert.Equal(0.2f + 0.4f * 0.70710678f, dst[1], 4);
    }

    [Fact]
    public void TwentyFourBitIsSignExtended()
    {
        var conv = new SampleConverter(new CaptureFormat(48000, 2, 24, false));
        byte[] src = { 0x00, 0x00, 0x80, 0xFF, 0xFF, 0x7F }; // -1.0, ~+1.0
        var dst = new float[2];
        conv.Convert(src, 1, dst);
        Assert.Equal(-1f, dst[0], 4);
        Assert.Equal(1f, dst[1], 4);
    }
}

public class GeometryTests
{
    [Theory]
    [InlineData(1920, 1080, 1920, 1080)]
    [InlineData(1279, 721, 1278, 720)]
    [InlineData(5120, 1440, 4096, 1152)]
    [InlineData(10, 10, 64, 64)]
    public void OutputSize(int w, int h, int ew, int eh)
    {
        Assert.Equal(new PixelSize(ew, eh), VideoGeometry.OutputSizeFor(w, h));
    }

    [Fact]
    public void FitLetterboxesWithoutStretching()
    {
        // A window grown wider than the recording: scaled down, bars top and bottom.
        var r = VideoGeometry.Fit(1600, 600, 800, 600);
        Assert.Equal(new PixelRect(0, 150, 800, 300), r);
        // A window shrunk: centered at 1:1, no upscale.
        Assert.Equal(new PixelRect(200, 150, 400, 300), VideoGeometry.Fit(400, 300, 800, 600));
    }

    [Fact]
    public void RegionIsClampedAndEven()
    {
        var r = VideoGeometry.NormalizeRegion(new PixelRect(-10, 5, 501, 301), 1920, 1080);
        Assert.Equal(0, r.X);
        Assert.Equal(0, r.Width % 2);
        Assert.Equal(0, r.Height % 2);
        Assert.True(r.Right <= 1920 && r.Bottom <= 1080);

        var tiny = VideoGeometry.NormalizeRegion(new PixelRect(1910, 1075, 5, 3), 1920, 1080);
        Assert.Equal(64, tiny.Width);
        Assert.Equal(64, tiny.Height);
        Assert.Equal(1920, tiny.Right);
        Assert.Equal(1080, tiny.Bottom);
    }

    [Fact]
    public void HighPresetKeepsTheOriginalBitrate()
    {
        int b1080p30 = EncoderSettings.BitrateFor(1920, 1080, 30, VideoQuality.High);
        int b1080p60 = EncoderSettings.BitrateFor(1920, 1080, 60, VideoQuality.High);
        Assert.InRange(b1080p30, 7_000_000, 8_000_000);
        Assert.InRange(b1080p60, b1080p30, b1080p30 * 2);
        Assert.Equal(EncoderSettings.MinBitrateFor(VideoQuality.High), EncoderSettings.BitrateFor(64, 64, 30, VideoQuality.High));
    }

    [Fact]
    public void CompactAndStandardHitTheirTargets()
    {
        Assert.InRange(EncoderSettings.BitrateFor(1920, 1080, 30, VideoQuality.Compact), 500_000, 1_000_000);
        Assert.InRange(EncoderSettings.BitrateFor(1920, 1080, 30, VideoQuality.Standard), 1_500_000, 2_000_000);

        // Ordering holds at every common size and frame rate.
        foreach (var (w, h) in new[] { (640, 480), (1280, 720), (1920, 1080), (2560, 1440), (3840, 2160) })
        foreach (int fps in new[] { 30, 60 })
        {
            int c = EncoderSettings.BitrateFor(w, h, fps, VideoQuality.Compact);
            int s = EncoderSettings.BitrateFor(w, h, fps, VideoQuality.Standard);
            int hi = EncoderSettings.BitrateFor(w, h, fps, VideoQuality.High);
            Assert.True(c < s && s < hi, $"{w}x{h}@{fps}: {c} / {s} / {hi}");
        }

        // A Compact 1080p30 class recording stays well under 0.5 GB per hour even at the full mean bitrate.
        int v = EncoderSettings.BitrateFor(1920, 1080, 30, VideoQuality.Compact);
        Assert.True(EncoderSettings.GigabytesPerHour(v, EncoderSettings.AudioBitrateFor(VideoQuality.Compact)) < 0.5);
    }

    [Fact]
    public void AudioBitratesAreOnesTheAacEncoderAccepts()
    {
        foreach (var q in Enum.GetValues<VideoQuality>())
        {
            int bytesPerSecond = EncoderSettings.AudioBitrateFor(q) / 8;
            Assert.Contains(bytesPerSecond, new[] { 12000, 16000, 20000, 24000 });
        }
        Assert.Equal(96_000, EncoderSettings.AudioBitrateFor(VideoQuality.Compact));
        Assert.Equal(128_000, EncoderSettings.AudioBitrateFor(VideoQuality.Standard));
    }

    [Fact]
    public void FrameScheduleIsExactOverLongRecordings()
    {
        var s = new FrameSchedule(30);
        long total = 0;
        for (int i = 0; i < 30 * 3600 * 3; i++) { total += s.NextDuration; s.Advance(); }
        Assert.Equal(3L * 3600 * 10_000_000, total);
        Assert.Equal(0, s.SkipIfBehind(s.NextTime + 2 * s.FrameInterval));
        Assert.Equal(10, s.SkipIfBehind(s.NextTime + 10 * s.FrameInterval + s.FrameInterval / 2));
    }

    [Fact]
    public void FrameSelectionPicksNewestNotLater()
    {
        long[] t = { 100, 300, 200, 400 };
        bool[] v = { true, true, true, false };
        Assert.Equal(1, FrameSelection.Choose(t, v, 350));
        Assert.Equal(2, FrameSelection.Choose(t, v, 250));
        Assert.Equal(0, FrameSelection.Choose(t, v, 50)); // all newer → oldest
        Assert.Equal(-1, FrameSelection.Choose(t, new bool[4], 50));
    }
}

public class FileNamingTests
{
    private static readonly DateTime T = new(2026, 10, 5, 10, 42, 31);

    [Theory]
    [InlineData(null, "Recording_2026-10-05_10-42-31.mp4")]
    [InlineData("Notes.txt - Notepad", "Notepad_2026-10-05_10-42-31.mp4")]
    [InlineData("General | Lecture | Microsoft Teams", "Microsoft-Teams_2026-10-05_10-42-31.mp4")]
    [InlineData("MyGame - SampleScene - Windows, Mac, Linux - Unity 6 (6000.0.23f1) <DX11>", "Unity-6_2026-10-05_10-42-31.mp4")]
    [InlineData("Unity", "Unity_2026-10-05_10-42-31.mp4")]
    [InlineData("a:b*c?", "abc_2026-10-05_10-42-31.mp4")]
    [InlineData("???", "Recording_2026-10-05_10-42-31.mp4")]
    public void BuildsUsefulNames(string? title, string expected)
    {
        Assert.Equal(expected, FileNaming.BuildFileName(title, T));
    }

    [Fact]
    public void NeverReusesAnExistingName()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine("C:", "R", "Recording_x.mp4"),
            Path.Combine("C:", "R", "Recording_x (2).recording.mp4"),
        };
        string p = FileNaming.MakeUnique(Path.Combine("C:", "R"), "Recording_x.mp4", existing.Contains);
        Assert.Equal(Path.Combine("C:", "R", "Recording_x (3).mp4"), p);
    }

    [Fact]
    public void InProgressNamesRoundTrip()
    {
        string f = Path.Combine("d", "Rec_1.mp4");
        string ip = FileNaming.InProgressPathFor(f);
        Assert.EndsWith(".recording.mp4", ip);
        Assert.Equal(f, FileNaming.FinalPathFor(ip));
    }

    [Fact]
    public void FriendlyMessagesForDiskFull()
    {
        Assert.Contains("disk is full", FriendlyErrors.ForWriteFailure(FriendlyErrors.ERROR_DISK_FULL));
        Assert.True(FriendlyErrors.IsDiskFull(FriendlyErrors.ERROR_HANDLE_DISK_FULL));
    }
}
