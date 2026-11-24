using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace SPBackup.Desktop;

/// <summary>Pre-rendered arrow frames; the database symbol stays still. Owns all cloned icons.</summary>
internal sealed class TrayAnimationIcons : IDisposable
{
    internal IReadOnlyList<Icon> Frames => frames;
    private readonly List<Icon> frames = [];
    internal TrayAnimationIcons(Icon original)
    {
        try
        {
            using var source = original.ToBitmap();
            var size = System.Windows.Forms.SystemInformation.SmallIconSize;
            for (var frame = 0; frame < 12; frame++)
            {
                using var large = new Bitmap(64, 64);
                using (var graphics = Graphics.FromImage(large))
                {
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.DrawImage(source, 0, 0, 64, 64);
                    var blue = source.GetPixel(0, source.Height / 2);
                    using var background = new SolidBrush(Color.FromArgb(255, blue.R, blue.G, blue.B));
                    graphics.FillEllipse(background, 33, 33, 31, 31);
                    var angle = frame * 30f;
                    using var arrow = new Pen(Color.FromArgb(51, 240, 160), 5) { StartCap = LineCap.Round };
                    graphics.DrawArc(arrow, 39, 39, 20, 20, angle, 270);
                    var end = (angle + 270) * Math.PI / 180;
                    var x = 49 + 10 * Math.Cos(end); var y = 49 + 10 * Math.Sin(end);
                    var tx = -Math.Sin(end); var ty = Math.Cos(end);
                    using var fill = new SolidBrush(arrow.Color);
                    graphics.FillPolygon(fill, [new PointF((float)(x + tx * 5), (float)(y + ty * 5)),
                        new PointF((float)(x - tx * 4 - ty * 5), (float)(y - ty * 4 + tx * 5)),
                        new PointF((float)(x - tx * 4 + ty * 5), (float)(y - ty * 4 - tx * 5))]);
                }
                using var bitmap = new Bitmap(size.Width, size.Height);
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.DrawImage(large, 0, 0, size.Width, size.Height);
                }
                var handle = bitmap.GetHicon();
                try { using var borrowed = Icon.FromHandle(handle); frames.Add((Icon)borrowed.Clone()); }
                finally { DestroyIcon(handle); }
            }
        }
        catch { Dispose(); throw; }
    }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
    public void Dispose() { foreach (var frame in frames) frame.Dispose(); frames.Clear(); }
}
