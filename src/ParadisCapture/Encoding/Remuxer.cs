using ParadisCapture.Services;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace ParadisCapture.Encoding;

/// <summary>
/// Copies the already-encoded H.264/AAC streams from the crash-safe fragmented MP4 into a regular
/// MP4 (with a single index), without re-encoding. Regular MP4 is what every editor and player
/// handles best. This is pure I/O: a 2-hour recording typically takes well under a minute.
/// </summary>
public static class Remuxer
{
    public static void Remux(string source, string destination, long expectedDurationHns, IProgress<double>? progress, CancellationToken cancel)
    {
        using var readerAttrs = MediaFactory.MFCreateAttributes(1);
        using var reader = MediaFactory.MFCreateSourceReaderFromURL(source, readerAttrs);

        using var writerAttrs = MediaFactory.MFCreateAttributes(1);
        writerAttrs.Set(TranscodeAttributeKeys.TranscodeContainertype, TranscodeContainerTypeGuids.Mpeg4);
        using var writer = MediaFactory.MFCreateSinkWriterFromURL(destination, null!, writerAttrs);

        var streamMap = new Dictionary<int, int>();
        for (int i = 0; ; i++)
        {
            IMFMediaType native;
            try
            {
                native = reader.GetNativeMediaType(i, 0);
            }
            catch (SharpGenException)
            {
                break; // MF_E_INVALIDSTREAMNUMBER: no more streams
            }

            using (native)
            {
                reader.SetStreamSelection(i, true);
                int outIndex = writer.AddStream(native);
                writer.SetInputMediaType(outIndex, native, null!); // same type in and out = passthrough
                streamMap[i] = outIndex;
            }
        }
        if (streamMap.Count == 0) throw new InvalidOperationException("No streams found in recording.");

        writer.BeginWriting();

        var finished = new HashSet<int>();
        long lastReport = -1;
        while (finished.Count < streamMap.Count)
        {
            cancel.ThrowIfCancellationRequested();

            using var sample = reader.ReadSample(MFGuids.MF_SOURCE_READER_ANY_STREAM, 0, out int stream, out SourceReaderFlag flags, out long timestamp);

            if ((flags & SourceReaderFlag.Error) != 0) throw new InvalidOperationException("Error while reading the recording.");
            if (!streamMap.TryGetValue(stream, out int outStream))
            {
                if ((flags & SourceReaderFlag.EndOfStream) != 0) finished.Add(stream);
                continue;
            }

            if ((flags & SourceReaderFlag.StreamTick) != 0) writer.SendStreamTick(outStream, timestamp);
            if (sample != null) writer.WriteSample(outStream, sample);
            if ((flags & SourceReaderFlag.EndOfStream) != 0) finished.Add(stream);

            if (progress != null && expectedDurationHns > 0)
            {
                long pct = Math.Clamp(timestamp * 100 / expectedDurationHns, 0, 100);
                if (pct != lastReport)
                {
                    lastReport = pct;
                    progress.Report(pct / 100.0);
                }
            }
        }

        writer.Finalize();
    }

    /// <summary>Duration of a media file in hns, or -1 if it can't be determined.</summary>
    public static long GetDuration(string path)
    {
        try
        {
            using var reader = MediaFactory.MFCreateSourceReaderFromURL(path, null!);
            var v = reader.GetPresentationAttribute(MFGuids.MF_SOURCE_READER_MEDIASOURCE, MFGuids.MF_PD_DURATION);
            return v.Value is ulong u ? (long)u : v.Value is long l ? l : -1;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read duration of {Path.GetFileName(path)}", ex);
            return -1;
        }
    }
}
