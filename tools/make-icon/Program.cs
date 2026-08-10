using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

// Renders the VR Flat Player mark at every size Windows asks for and packs the
// result into a multi-resolution .ico. See make-icon.csproj for why this is not
// just an SVG conversion.

var root = FindRepoRoot();
var assets = Path.Combine(root, "assets");
Directory.CreateDirectory(assets);

// 256 and 128 go in as PNG (the Vista .ico extension, far smaller); everything
// at or below 64 goes in as a classic DIB, because a few older shell surfaces
// and icon readers still skip PNG entries.
int[] sizes = [256, 128, 64, 48, 40, 32, 24, 20, 16];

var ico = Path.Combine(assets, "icon.ico");
WriteIco(ico, sizes, pngAtOrAbove: 128);
Console.WriteLine($"{ico}  ({sizes.Length} sizes, {new FileInfo(ico).Length / 1024} KB)");

// Flat PNGs for the README, the release page and anywhere that wants a bitmap.
foreach (var size in new[] { 1024, 512, 256 })
{
    var path = Path.Combine(assets, $"icon-{size}.png");
    using var bmp = Render(size);
    bmp.Save(path, ImageFormat.Png);
    Console.WriteLine(path);
}

// A contact sheet so the shrink-down can be eyeballed in one look.
var sheet = Path.Combine(assets, "icon-sizes.png");
WriteContactSheet(sheet, [256, 128, 64, 48, 32, 24, 16]);
Console.WriteLine(sheet);

return 0;

// --------------------------------------------------------------- the mark ---

static Bitmap Render(int size)
{
    var geom = Geom.For(size);

    var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bmp);
    Quality(g);
    g.Clear(Color.Transparent);

    var scale = size / 256f;
    g.ScaleTransform(scale, scale);

    // Tile.
    using var tile = RoundedRect(0, 0, 256, 256, 56);
    using (var fill = new LinearGradientBrush(new PointF(0, 0), new PointF(256, 256),
                                              Hex("#312E81"), Hex("#7C3AED")))
    {
        fill.InterpolationColors = new ColorBlend
        {
            Colors = [Hex("#312E81"), Hex("#5B21B6"), Hex("#7C3AED")],
            Positions = [0f, 0.55f, 1f],
        };
        g.FillPath(fill, tile);
    }

    // Highlight from the top left, clipped to the tile.
    g.SetClip(tile);
    const float gx = 0.28f * 256, gy = 0.20f * 256, gr = 0.85f * 256;
    using (var halo = new GraphicsPath())
    {
        halo.AddEllipse(gx - gr, gy - gr, gr * 2, gr * 2);
        using var glow = new PathGradientBrush(halo)
        {
            CenterPoint = new PointF(gx, gy),
            CenterColor = Color.FromArgb(128, 0xA7, 0x8B, 0xFA),
            SurroundColors = [Color.FromArgb(0, 0xA7, 0x8B, 0xFA)],
        };
        g.FillPath(glow, halo);
    }
    g.ResetClip();

    // Tracking field: an arc either side of the visor. Doing double duty as the
    // head strap is what earns them their place -- one shape, two readings, and
    // the strap is what stops the visor looking like a television.
    if (geom.ArcRadius > 0)
    {
        var box = 128 - geom.ArcRadius;
        var d = geom.ArcRadius * 2;
        using var pen = new Pen(Hex("#22D3EE"), geom.ArcStroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawArc(pen, box, box, d, d, -geom.ArcSweep / 2, geom.ArcSweep);
        g.DrawArc(pen, box, box, d, d, 180 - geom.ArcSweep / 2, geom.ArcSweep);
    }

    using (var visor = VisorPath(geom))
    {
        g.FillPath(Brushes.White, visor);
    }

    using (var tri = TrianglePath(geom))
    using (var brush = new SolidBrush(Hex("#3B0F82")))
    using (var pen = new Pen(Hex("#3B0F82"), geom.TriPen) { LineJoin = LineJoin.Round })
    {
        g.FillPath(brush, tri);
        g.DrawPath(pen, tri);
    }

    return bmp;
}

/// <summary>
/// The headset: a rounded rectangle whose bottom edge lifts into a nose bridge.
/// The notch is part of the outline rather than a subtracted region because
/// GDI+ region arithmetic does not antialias.
/// </summary>
static GraphicsPath VisorPath(Geom geom)
{
    float l = geom.VisorX, t = geom.VisorY;
    float r = l + geom.VisorW, b = t + geom.VisorH;
    var d = geom.VisorRadius * 2;

    float nl = 128 - geom.NoseHalfWidth, nr = 128 + geom.NoseHalfWidth;
    var apex = b - geom.NoseDepth;

    var p = new GraphicsPath();
    p.AddArc(l, t, d, d, 180, 90);              // top left
    p.AddArc(r - d, t, d, d, 270, 90);          // top right
    p.AddArc(r - d, b - d, d, d, 0, 90);        // bottom right
    p.AddLine(nr, b, nr, b);
    p.AddBezier(nr, b, nr - geom.NoseHalfWidth * 0.5f, b, 128 + geom.NoseHalfWidth * 0.25f, apex, 128, apex);
    p.AddBezier(128, apex, 128 - geom.NoseHalfWidth * 0.25f, apex, nl + geom.NoseHalfWidth * 0.5f, b, nl, b);
    p.AddArc(l, b - d, d, d, 90, 90);           // bottom left
    p.CloseFigure();
    return p;
}

// Authored undersized; the same-colour round-join stroke inflates it by roughly
// half the pen width per edge and is what gives the corners their radius.
static GraphicsPath TrianglePath(Geom geom)
{
    var tri = new GraphicsPath();
    tri.AddPolygon([
        new PointF(geom.TriBase, geom.TriY - geom.TriHalfHeight),
        new PointF(geom.TriApex, geom.TriY),
        new PointF(geom.TriBase, geom.TriY + geom.TriHalfHeight),
    ]);
    return tri;
}

static void Quality(Graphics g)
{
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    g.CompositingQuality = CompositingQuality.HighQuality;
    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
}

static GraphicsPath RoundedRect(float x, float y, float w, float h, float radius)
{
    var d = radius * 2;
    var p = new GraphicsPath();
    p.AddArc(x, y, d, d, 180, 90);
    p.AddArc(x + w - d, y, d, d, 270, 90);
    p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
    p.AddArc(x, y + h - d, d, d, 90, 90);
    p.CloseFigure();
    return p;
}

static Color Hex(string s) => ColorTranslator.FromHtml(s);

// ------------------------------------------------------------- .ico output ---

static void WriteIco(string path, int[] sizes, int pngAtOrAbove)
{
    var images = sizes
        .Select(size =>
        {
            using var bmp = Render(size);
            return (size, bytes: size >= pngAtOrAbove ? AsPng(bmp) : AsDib(bmp));
        })
        .ToArray();

    using var file = File.Create(path);
    using var w = new BinaryWriter(file);

    w.Write((short)0);                  // reserved
    w.Write((short)1);                  // type: icon
    w.Write((short)images.Length);

    var offset = 6 + 16 * images.Length;
    foreach (var (size, bytes) in images)
    {
        w.Write((byte)(size >= 256 ? 0 : size));   // 0 means 256
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)0);               // palette size
        w.Write((byte)0);               // reserved
        w.Write((short)1);              // colour planes
        w.Write((short)32);             // bits per pixel
        w.Write(bytes.Length);
        w.Write(offset);
        offset += bytes.Length;
    }

    foreach (var (_, bytes) in images) w.Write(bytes);
}

static byte[] AsPng(Bitmap bmp)
{
    using var ms = new MemoryStream();
    bmp.Save(ms, ImageFormat.Png);
    return ms.ToArray();
}

// A BITMAPINFOHEADER with doubled height, bottom-up BGRA, then the 1bpp AND
// mask. The mask is redundant for a 32bpp icon but the format still requires
// the bytes to be there.
static byte[] AsDib(Bitmap bmp)
{
    int w = bmp.Width, h = bmp.Height;
    using var ms = new MemoryStream();
    using var bw = new BinaryWriter(ms);

    bw.Write(40);                       // biSize
    bw.Write(w);
    bw.Write(h * 2);                    // colour rows + mask rows
    bw.Write((short)1);                 // biPlanes
    bw.Write((short)32);                // biBitCount
    bw.Write(0);                        // BI_RGB
    bw.Write(0);                        // biSizeImage
    bw.Write(0); bw.Write(0);           // pixels per metre
    bw.Write(0); bw.Write(0);           // palette counts

    var locked = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    try
    {
        var row = new byte[w * 4];
        for (var y = h - 1; y >= 0; y--)
        {
            System.Runtime.InteropServices.Marshal.Copy(locked.Scan0 + y * locked.Stride, row, 0, row.Length);
            bw.Write(row);
        }
    }
    finally
    {
        bmp.UnlockBits(locked);
    }

    bw.Write(new byte[(w + 31) / 32 * 4 * h]);
    bw.Flush();
    return ms.ToArray();
}

// ------------------------------------------------------------ contact sheet ---

static void WriteContactSheet(string path, int[] sizes)
{
    const int pad = 24;
    var width = sizes.Sum() + pad * (sizes.Length + 1);
    var height = sizes.Max() + pad * 2;

    using var sheet = new Bitmap(width, height, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(sheet);
    Quality(g);
    g.Clear(Color.FromArgb(0x11, 0x11, 0x14));

    var x = pad;
    foreach (var size in sizes)
    {
        using var bmp = Render(size);
        g.DrawImageUnscaled(bmp, x, (height - size) / 2);
        x += size + pad;
    }

    sheet.Save(path, ImageFormat.Png);
}

// ------------------------------------------------------------------- paths ---

static string FindRepoRoot()
{
    var dir = AppContext.BaseDirectory;
    while (dir is not null)
    {
        if (Directory.Exists(Path.Combine(dir, "src")) && Directory.Exists(Path.Combine(dir, "tools")))
            return dir;
        dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
    }
    throw new DirectoryNotFoundException("could not find the repository root above " + AppContext.BaseDirectory);
}

// ---------------------------------------------------------------- geometry ---

/// <summary>
/// The mark on a 256-unit grid. Two sets of numbers exist because an icon is
/// not one drawing scaled down: below about 32px the tracking arcs are a
/// hairline and the nose bridge closes up, so the small variant drops the arcs
/// and gives the remaining shapes the whole tile.
/// </summary>
internal readonly record struct Geom(
    float ArcRadius, float ArcStroke, float ArcSweep,
    float VisorX, float VisorY, float VisorW, float VisorH, float VisorRadius,
    float NoseHalfWidth, float NoseDepth,
    float TriBase, float TriApex, float TriY, float TriHalfHeight, float TriPen)
{
    public static readonly Geom Large = new(
        ArcRadius: 105, ArcStroke: 15, ArcSweep: 70,
        VisorX: 44, VisorY: 76, VisorW: 168, VisorH: 104, VisorRadius: 36,
        NoseHalfWidth: 24, NoseDepth: 26,
        TriBase: 106, TriApex: 168, TriY: 124, TriHalfHeight: 26, TriPen: 12);

    public static readonly Geom Small = new(
        ArcRadius: 0, ArcStroke: 0, ArcSweep: 0,
        VisorX: 30, VisorY: 66, VisorW: 196, VisorH: 124, VisorRadius: 42,
        NoseHalfWidth: 30, NoseDepth: 34,
        TriBase: 102, TriApex: 174, TriY: 122, TriHalfHeight: 31, TriPen: 14);

    public static Geom For(int size) => size <= 32 ? Small : Large;
}
