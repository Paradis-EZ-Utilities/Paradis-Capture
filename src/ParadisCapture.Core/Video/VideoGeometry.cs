namespace ParadisCapture.Core.Video;

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static PixelRect FromPoints(int x1, int y1, int x2, int y2) =>
        new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));

    public PixelRect Intersect(PixelRect other)
    {
        int x = Math.Max(X, other.X), y = Math.Max(Y, other.Y);
        int r = Math.Min(Right, other.Right), b = Math.Min(Bottom, other.Bottom);
        return r > x && b > y ? new PixelRect(x, y, r - x, b - y) : new PixelRect(x, y, 0, 0);
    }

    public override string ToString() => $"{Width} x {Height} at ({X}, {Y})";
}

public readonly record struct PixelSize(int Width, int Height)
{
    public override string ToString() => $"{Width} x {Height}";
}

/// <summary>Size/placement rules for H.264 output.</summary>
public static class VideoGeometry
{
    /// <summary>Largest frame we ask the encoder for. Hardware H.264 encoders commonly top out at 4096.</summary>
    public const int MaxDimension = 4096;

    /// <summary>Smallest frame we produce; tiny frames are padded (letterboxed) up to this.</summary>
    public const int MinDimension = 64;

    /// <summary>
    /// Output size for a source: native size, rounded down to even numbers (NV12/H.264 need
    /// even dimensions), scaled down proportionally only if it exceeds what encoders accept.
    /// </summary>
    public static PixelSize OutputSizeFor(int sourceWidth, int sourceHeight)
    {
        double w = Math.Max(1, sourceWidth), h = Math.Max(1, sourceHeight);
        double scale = Math.Min(1.0, Math.Min(MaxDimension / w, MaxDimension / h));
        int ow = MakeEven((int)Math.Floor(w * scale));
        int oh = MakeEven((int)Math.Floor(h * scale));
        return new PixelSize(Math.Max(MinDimension, ow), Math.Max(MinDimension, oh));
    }

    public static int MakeEven(int v) => Math.Max(2, v & ~1);

    /// <summary>
    /// Largest rectangle with the source aspect ratio that fits (centered) inside the destination.
    /// Never upscales beyond 1:1 unless <paramref name="allowUpscale"/>; the rest is black bars.
    /// </summary>
    public static PixelRect Fit(int sourceWidth, int sourceHeight, int destWidth, int destHeight, bool allowUpscale = false)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0) return new PixelRect(0, 0, destWidth, destHeight);
        double scale = Math.Min((double)destWidth / sourceWidth, (double)destHeight / sourceHeight);
        if (!allowUpscale) scale = Math.Min(scale, 1.0);
        int w = Math.Clamp((int)Math.Round(sourceWidth * scale), 1, destWidth);
        int h = Math.Clamp((int)Math.Round(sourceHeight * scale), 1, destHeight);
        int x = (destWidth - w) / 2;
        int y = (destHeight - h) / 2;
        return new PixelRect(x, y, w, h);
    }

    /// <summary>
    /// Normalizes a user-dragged region (monitor-relative physical pixels): clamps to the monitor,
    /// enforces a minimum size, and makes the dimensions even so no scaling is needed.
    /// </summary>
    public static PixelRect NormalizeRegion(PixelRect region, int monitorWidth, int monitorHeight)
    {
        var bounds = new PixelRect(0, 0, monitorWidth, monitorHeight);
        var r = region.Intersect(bounds);
        int w = Math.Min(Math.Max(r.Width, MinDimension), monitorWidth);
        int h = Math.Min(Math.Max(r.Height, MinDimension), monitorHeight);
        w = MakeEven(w);
        h = MakeEven(h);
        // Grow around the center if we had to enlarge, then keep inside the monitor.
        int x = r.X - (w - r.Width) / 2;
        int y = r.Y - (h - r.Height) / 2;
        x = Math.Clamp(x, 0, Math.Max(0, monitorWidth - w));
        y = Math.Clamp(y, 0, Math.Max(0, monitorHeight - h));
        return new PixelRect(x, y, w, h);
    }
}
