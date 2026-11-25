namespace DeskRecall.Core.Geometry;

/// <summary>Ganzzahliges Rechteck in physischen Bildschirmpixeln (Left/Top inklusiv, Right/Bottom exklusiv).</summary>
public readonly record struct RectI(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public long Area => IsEmpty ? 0 : (long)Width * Height;
    public double CenterX => Left + Width / 2.0;
    public double CenterY => Top + Height / 2.0;

    public static RectI FromSize(int left, int top, int width, int height) => new(left, top, left + width, top + height);

    public RectI Offset(int dx, int dy) => new(Left + dx, Top + dy, Right + dx, Bottom + dy);

    public RectI Intersect(RectI other)
    {
        var l = Math.Max(Left, other.Left);
        var t = Math.Max(Top, other.Top);
        var r = Math.Min(Right, other.Right);
        var b = Math.Min(Bottom, other.Bottom);
        return r <= l || b <= t ? default : new RectI(l, t, r, b);
    }

    public bool Contains(RectI other) =>
        other.Left >= Left && other.Top >= Top && other.Right <= Right && other.Bottom <= Bottom;

    public bool Contains(double x, double y) => x >= Left && x < Right && y >= Top && y < Bottom;

    public double DistanceTo(double x, double y)
    {
        var dx = x < Left ? Left - x : x >= Right ? x - (Right - 1) : 0;
        var dy = y < Top ? Top - y : y >= Bottom ? y - (Bottom - 1) : 0;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public override string ToString() => $"[{Left},{Top} {Width}×{Height}]";
}
