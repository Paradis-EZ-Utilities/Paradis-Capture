using System.Runtime.InteropServices;
using ParadisCapture.Capture;
using ParadisCapture.Services;
using Vortice.MediaFoundation;

namespace ParadisCapture.Encoding;

public enum EncoderMode
{
    /// <summary>Hardware encoder fed directly with GPU textures (zero-copy).</summary>
    GpuTextures,
    /// <summary>GPU color conversion, frames copied to system memory, encoder chosen by Windows (usually still hardware).</summary>
    SystemMemory,
    /// <summary>System memory and Microsoft's software H.264 encoder only.</summary>
    Software,
}

/// <summary>Output stream settings. <paramref name="AudioBitrate"/> is the AAC bitrate (bits/s) used when audio is recorded.</summary>
public sealed record VideoStreamFormat(int Width, int Height, int Fps, int Bitrate, int GopSize, int AudioBitrate = 192_000);

/// <summary>
/// Media Foundation Sink Writer producing H.264 + AAC in MP4. Samples are encoded and written to
/// disk progressively; nothing accumulates in memory.
///
/// <para>By default the container is fragmented MP4: the file is valid and playable at every
/// fragment boundary (every ~2 s), so a crash or power loss doesn't destroy the recording.
/// <see cref="Remuxer"/> converts it to a regular MP4 when recording ends.</para>
/// </summary>
public sealed class MediaWriter : IDisposable
{
    public const int AudioSampleRate = 48000;
    public const int AudioChannels = 2;

    private readonly object _lock = new();
    private IMFSinkWriter? _writer;
    private readonly int _videoStream;
    private readonly int _audioStream = -1;
    private bool _finalized;

    public EncoderMode Mode { get; }
    public bool Fragmented { get; }
    public VideoStreamFormat Video { get; }
    public bool HasAudio => _audioStream >= 0;

    private MediaWriter(IMFSinkWriter writer, int videoStream, int audioStream, EncoderMode mode, bool fragmented, VideoStreamFormat video)
    {
        _writer = writer;
        _videoStream = videoStream;
        _audioStream = audioStream;
        Mode = mode;
        Fragmented = fragmented;
        Video = video;
    }

    /// <summary>
    /// Creates the writer. <paramref name="gfx"/> is required for <see cref="EncoderMode.GpuTextures"/>.
    /// </summary>
    public static MediaWriter Create(string path, VideoStreamFormat video, bool withAudio, EncoderMode mode, GraphicsDevice? gfx, bool fragmented)
    {
        using var attributes = MediaFactory.MFCreateAttributes(4);
        if (mode == EncoderMode.GpuTextures)
        {
            if (gfx == null) throw new ArgumentNullException(nameof(gfx));
            attributes.Set(SinkWriterAttributeKeys.D3DManager, gfx.DeviceManager);
        }
        attributes.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, mode == EncoderMode.Software ? 0u : 1u);
        attributes.Set(TranscodeAttributeKeys.TranscodeContainertype, fragmented ? MFGuids.ContainerFragmentedMp4 : TranscodeContainerTypeGuids.Mpeg4);

        IMFSinkWriter writer = MediaFactory.MFCreateSinkWriterFromURL(path, null!, attributes);
        try
        {
            int videoStream;
            using (var outType = CreateH264Type(video))
            {
                videoStream = writer.AddStream(outType);
            }
            using (var inType = CreateNv12Type(video))
            {
                SetInputWithFallback(writer, videoStream, inType, CreateVideoEncodingParameters(video));
            }

            int audioStream = -1;
            if (withAudio)
            {
                audioStream = AddAacStream(writer, video.AudioBitrate);
                using var pcmType = CreatePcmType();
                writer.SetInputMediaType(audioStream, pcmType, null!);
            }

            writer.BeginWriting();
            return new MediaWriter(writer, videoStream, audioStream, mode, fragmented, video);
        }
        catch
        {
            writer.Dispose();
            TryDelete(path);
            throw;
        }
    }

    private static void SetInputWithFallback(IMFSinkWriter writer, int stream, IMFMediaType inType, IMFAttributes encodingParameters)
    {
        try
        {
            writer.SetInputMediaType(stream, inType, encodingParameters);
        }
        catch (Exception ex)
        {
            // Some encoders reject a property (e.g. rate control mode). Their defaults are fine.
            Log.Warn("Encoder rejected custom settings; using encoder defaults", ex);
            writer.SetInputMediaType(stream, inType, null!);
        }
        finally
        {
            encodingParameters.Dispose();
        }
    }

    private static IMFMediaType CreateH264Type(VideoStreamFormat v)
    {
        var t = MediaFactory.MFCreateMediaType();
        t.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        t.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
        t.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)v.Bitrate);
        t.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)2 /* MFVideoInterlace_Progressive */);
        t.Set(MediaTypeAttributeKeys.Mpeg2Profile, (uint)100 /* eAVEncH264VProfile_High */);
        MediaFactory.MFSetAttributeSize(t, MediaTypeAttributeKeys.FrameSize, (uint)v.Width, (uint)v.Height);
        MediaFactory.MFSetAttributeRatio(t, MediaTypeAttributeKeys.FrameRate, (uint)v.Fps, 1);
        MediaFactory.MFSetAttributeRatio(t, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1);
        SetColorAttributes(t);
        return t;
    }

    private static IMFMediaType CreateNv12Type(VideoStreamFormat v)
    {
        var t = MediaFactory.MFCreateMediaType();
        t.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        t.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
        t.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)2);
        t.Set(MediaTypeAttributeKeys.AllSamplesIndependent, 1u);
        t.Set(MediaTypeAttributeKeys.DefaultStride, (uint)v.Width);
        MediaFactory.MFSetAttributeSize(t, MediaTypeAttributeKeys.FrameSize, (uint)v.Width, (uint)v.Height);
        MediaFactory.MFSetAttributeRatio(t, MediaTypeAttributeKeys.FrameRate, (uint)v.Fps, 1);
        MediaFactory.MFSetAttributeRatio(t, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1);
        SetColorAttributes(t);
        return t;
    }

    /// <summary>BT.709 limited range, matching what the video processor produces.</summary>
    private static void SetColorAttributes(IMFMediaType t)
    {
        t.Set(MediaTypeAttributeKeys.VideoPrimaries, (uint)2 /* MFVideoPrimaries_BT709 */);
        t.Set(MediaTypeAttributeKeys.TransferFunction, (uint)5 /* MFVideoTransFunc_709 */);
        t.Set(MediaTypeAttributeKeys.YuvMatrix, (uint)1 /* MFVideoTransferMatrix_BT709 */);
        t.Set(MediaTypeAttributeKeys.VideoNominalRange, (uint)2 /* MFNominalRange_16_235 */);
    }

    private static IMFAttributes CreateVideoEncodingParameters(VideoStreamFormat v)
    {
        var p = MediaFactory.MFCreateAttributes(4);
        // Unconstrained VBR at the computed mean bitrate: static screens (lectures) cost far less
        // than the mean, busy scenes (games) may briefly use more.
        p.Set(MFGuids.CODECAPI_AVEncCommonRateControlMode, (uint)2 /* eAVEncCommonRateControlMode_UnconstrainedVBR */);
        p.Set(MFGuids.CODECAPI_AVEncCommonMeanBitRate, (uint)v.Bitrate);
        p.Set(MFGuids.CODECAPI_AVEncMPVGOPSize, (uint)v.GopSize);
        p.Set(MFGuids.CODECAPI_AVLowLatencyMode, 0u);
        return p;
    }

    private static int AddAacStream(IMFSinkWriter writer, int bitrate)
    {
        try
        {
            using var aacType = CreateAacType(bitrate);
            return writer.AddStream(aacType);
        }
        catch (Exception ex) when (bitrate != 192_000)
        {
            // 192 kbit/s is what earlier builds always used, so it is the known-good fallback.
            Log.Warn($"AAC encoder rejected {bitrate / 1000} kbit/s; using 192 kbit/s", ex);
            using var aacType = CreateAacType(192_000);
            return writer.AddStream(aacType);
        }
    }

    private static IMFMediaType CreateAacType(int bitrate)
    {
        var t = MediaFactory.MFCreateMediaType();
        t.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
        t.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac);
        t.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
        t.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)AudioSampleRate);
        t.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)AudioChannels);
        // The AAC encoder only accepts 12000, 16000, 20000 or 24000 bytes/s (96–192 kbit/s).
        uint bytesPerSecond = (uint)Math.Clamp(bitrate / 8 / 4000 * 4000, 12000, 24000);
        t.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, bytesPerSecond);
        t.Set(MediaTypeAttributeKeys.AacAudioProfileLevelIndication, 0x29u); // AAC-LC
        return t;
    }

    private static IMFMediaType CreatePcmType()
    {
        var t = MediaFactory.MFCreateMediaType();
        t.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
        t.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
        t.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
        t.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)AudioSampleRate);
        t.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)AudioChannels);
        t.Set(MediaTypeAttributeKeys.AudioBlockAlignment, (uint)(AudioChannels * 2));
        t.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(AudioSampleRate * AudioChannels * 2));
        t.Set(MediaTypeAttributeKeys.AllSamplesIndependent, 1u);
        return t;
    }

    /// <summary>Writes one video sample (timestamps already set). Thread-safe.</summary>
    public void WriteVideo(IMFSample sample)
    {
        lock (_lock)
        {
            if (_writer == null || _finalized) return;
            _writer.WriteSample(_videoStream, sample);
        }
    }

    /// <summary>Writes interleaved 16-bit PCM. Thread-safe.</summary>
    public unsafe void WriteAudio(ReadOnlySpan<short> pcm, long timestamp, long duration)
    {
        if (_audioStream < 0 || pcm.IsEmpty) return;
        int bytes = pcm.Length * 2;
        using var buffer = MediaFactory.MFCreateMemoryBuffer(bytes);
        buffer.Lock(out IntPtr data, out _, out _);
        try
        {
            fixed (short* src = pcm)
            {
                Buffer.MemoryCopy(src, (void*)data, bytes, bytes);
            }
        }
        finally
        {
            buffer.Unlock();
        }
        buffer.CurrentLength = bytes;

        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = timestamp;
        sample.SampleDuration = duration;

        lock (_lock)
        {
            if (_writer == null || _finalized) return;
            _writer.WriteSample(_audioStream, sample);
        }
    }

    /// <summary>Flushes the encoders and closes the file. Safe to call once; later calls do nothing.</summary>
    public void Finish()
    {
        lock (_lock)
        {
            if (_writer == null || _finalized) return;
            _finalized = true;
            _writer.Finalize();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    internal static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}

/// <summary>GUIDs not exposed by Vortice (values from mfapi.h / codecapi.h).</summary>
internal static class MFGuids
{
    public static readonly Guid ContainerFragmentedMp4 = new("9ba876f1-419f-4b77-a1e0-35959d9d4004"); // MFTranscodeContainerType_FMPEG4
    public static readonly Guid CODECAPI_AVEncCommonRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    public static readonly Guid CODECAPI_AVEncCommonMeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    public static readonly Guid CODECAPI_AVEncMPVGOPSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    public static readonly Guid CODECAPI_AVLowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    public static readonly Guid MF_PD_DURATION = new("6c990d33-bb8e-477a-8598-0d5d96fcd88a");
    public const int MF_E_SAMPLEALLOCATOR_EMPTY = unchecked((int)0xC00D4A3E);
    public const int MF_SOURCE_READER_ANY_STREAM = unchecked((int)0xFFFFFFFE);
    public const int MF_SOURCE_READER_MEDIASOURCE = unchecked((int)0xFFFFFFFF);
}
