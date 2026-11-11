using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace PodBattery;

/// <summary>Draws the tray icon: a coloured rounded square with the battery number on it.</summary>
internal static class IconRenderer
{
    public static Icon Render(string text, Color background, Color foreground)
    {
        var size = SystemInformation.SmallIconSize.Width;
        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using (var square = RoundedSquare(size, size * 0.22f))
            using (var fill = new SolidBrush(background))
                g.FillPath(fill, square);

            using var glyphs = new GraphicsPath();
            using var family = new FontFamily("Segoe UI");
            glyphs.AddString(text, family, (int)FontStyle.Bold, size, PointF.Empty, StringFormat.GenericTypographic);

            // Scale the text to fill the square, centred on its actual glyph outlines.
            var bounds = glyphs.GetBounds();
            var scale = Math.Min(size * 0.86f / bounds.Width, size * 0.64f / bounds.Height);
            using var transform = new Matrix();
            transform.Translate(size / 2f, size / 2f);
            transform.Scale(scale, scale);
            transform.Translate(-(bounds.X + bounds.Width / 2), -(bounds.Y + bounds.Height / 2));
            glyphs.Transform(transform);

            using var ink = new SolidBrush(foreground);
            g.FillPath(ink, glyphs);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static GraphicsPath RoundedSquare(int size, float radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(0, 0, d, d, 180, 90);
        path.AddArc(size - d, 0, d, d, 270, 90);
        path.AddArc(size - d, size - d, d, d, 0, 90);
        path.AddArc(0, size - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
