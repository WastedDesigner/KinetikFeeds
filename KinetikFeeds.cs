// Kinetik Feeds: an RSS and Atom headlines desktop widget in the style of Kinetik's widget, Kinetik Calendar and Kinetik Clock.
// Feeds are fetched straight from their public links: no account, no API keys.
// Copyright © 2026 Akila Sella Hennedige. GPL-3.0-or-later.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using System.Xml;
using Microsoft.Win32;

[assembly: AssemblyTitle("Kinetik Feeds")]
[assembly: AssemblyProduct("Kinetik Feeds")]
[assembly: AssemblyCopyright("Copyright © 2026 Akila Sella Hennedige")]
[assembly: AssemblyVersion("1.1.3.0")]

static class Program
{
    public const string Version = "1.1.3";
    // --demo: made-up feeds and headlines, nothing fetched, read from or saved to this PC.
    // "--theme Name" picks its theme (for screenshots).
    public static bool Demo;
    public static string DemoTheme;
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    [STAThread]
    static void Main(string[] args)
    {
        Demo = args.Contains("--demo");
        int ti = Array.IndexOf(args, "--theme");
        if (ti >= 0 && ti + 1 < args.Length) DemoTheme = args[ti + 1];
        bool created;
        using (new Mutex(true, Demo ? @"Local\KinetikFeedsDemo" : @"Local\KinetikFeeds", out created))
        {
            if (!created) return; // already running
            SetProcessDPIAware();
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            ToolStripManager.Renderer = new DarkMenu();
            Backup.KeyName = "KinetikFeeds"; Backup.AppTitle = "Kinetik Feeds";
            if (!Demo) Backup.OfferRestore();
            var app = new FeedsForm(args.Contains("--settings"));
            using (new KinetikSuite.Shutdown("KinetikFeeds" + (Demo ? "Demo" : ""), () => { app.Shutdown(); Application.Exit(); }))
                Application.Run(app);
        }
        if (!Demo) Backup.AfterExit();
    }
}

// ======================================================================= Settings
class Source
{
    public string Name = "", Url = "";
    public Color Color = Rss.Palette[0];
    public bool On = true;
}

class Settings
{
    const string KeyPath = @"Software\KinetikFeeds";
    public int X = int.MinValue, Y = int.MinValue, Width = 340, Scale = 100, Opacity = CommonMenu.DefaultOpacity, Round = 100, Layer = 0;
    public int MaxItems = 15, RefreshMin = 30, MaxHeight = 520, MaxAgeDays = 0, Accent;
    public bool Locked, Snap = true, ClickThrough, ShowSource = true, TwoLines = true, Shadow = true, Border = true, HideRead, ShowHeader = true, UnreadBadge = true;
    public string ThemeName = "Midnight", FontName = "", Title = "Feeds";
    public string Feeds = "BBC News\thttps://feeds.bbci.co.uk/news/world/rss.xml\nThe Verge\thttps://www.theverge.com/rss/index.xml\nHacker News\thttps://hnrss.org/frontpage";

    // One feed per line: name, link, colour (ARGB) and on/off, separated by tabs. Lines from v1.0 have just the first two.
    public List<Source> List
    {
        get
        {
            var list = new List<Source>();
            foreach (var l in Feeds.Split('\n'))
            {
                var p = l.Split('\t');
                if (p.Length < 2) continue;
                var s = new Source { Name = p[0], Url = p[1].Trim(), Color = Rss.Palette[list.Count % Rss.Palette.Length] };
                int argb;
                if (p.Length > 2 && int.TryParse(p[2], out argb)) s.Color = Color.FromArgb(255, Color.FromArgb(argb));
                if (p.Length > 3) s.On = p[3].Trim() != "0";
                list.Add(s);
            }
            return list;
        }
        set
        {
            Feeds = string.Join("\n", value.Select(s => Clean(s.Name) + "\t" + Clean(s.Url) + "\t" + s.Color.ToArgb() + "\t" + (s.On ? 1 : 0)));
        }
    }

    static string Clean(string s) { return (s ?? "").Replace("\t", " ").Replace("\n", " ").Replace("\r", ""); }

    static IEnumerable<FieldInfo> Fields
    {
        get
        {
            return typeof(Settings).GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Where(f => f.FieldType == typeof(int) || f.FieldType == typeof(bool) || f.FieldType == typeof(string));
        }
    }

    static readonly string[] LookFields = { "Width", "Scale", "Opacity", "Round", "Shadow", "Border", "ThemeName", "FontName", "Accent", "MaxHeight" };

    public void ResetLook()
    {
        var d = new Settings();
        foreach (var f in Fields.Where(f => LookFields.Contains(f.Name))) f.SetValue(this, f.GetValue(d));
    }

    // Set when the saved settings exist but couldn't be read. The app then runs on defaults, and Save does nothing,
    // so those defaults can never overwrite the real settings. Restarting the app tries again.
    public static bool Unreadable;

    // Reads the settings, retrying a few times: the key can be briefly unreadable, e.g. while a backup is being
    // restored into it or another program has it busy.
    public static Settings Load()
    {
        if (Program.Demo) return DemoData.Settings();
        Settings s = null;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (attempt > 0) Thread.Sleep(500);
            bool ok;
            s = TryLoad(out ok);
            if (ok) { Unreadable = false; s.Opacity = CommonMenu.Migrate(s.Opacity); return s; }
        }
        Unreadable = true;
        MessageBox.Show("Kinetik Feeds couldn't read its saved settings, so it's showing the default feeds and look for now.\n\n" +
            "Your settings haven't been changed, and nothing will be saved over them while this is showing. " +
            "Restart Kinetik Feeds to try again" + (Backup.Exists ? ", or use Settings → Behaviour → Restore." : "."),
            "Kinetik Feeds", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return s;
    }

    // ok is false when the settings are there but couldn't be read (a missing key is fine: first run).
    static Settings TryLoad(out bool ok)
    {
        ok = true;
        var s = new Settings();
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(KeyPath))
                if (k != null)
                    foreach (var f in Fields)
                    {
                        var v = k.GetValue(f.Name);
                        if (v == null) continue;
                        try
                        {
                            if (f.FieldType == typeof(string)) f.SetValue(s, v.ToString());
                            else if (f.FieldType == typeof(int)) f.SetValue(s, Convert.ToInt32(v));
                            else f.SetValue(s, Convert.ToInt32(v) != 0);
                        }
                        catch { ok = false; }
                    }
        }
        catch { ok = false; }
        s.RefreshMin = Math.Max(5, s.RefreshMin);
        KinetikSuite.SettingsValidation.Sanitize(s); return s;
    }

    public void Save()
    {
        if (Program.Demo || Unreadable) return;
        KinetikSuite.SettingsValidation.Sanitize(this);
        if (KinetikSuite.RegistryFiles.CannotSave) return; // never save defaults over settings that couldn't be read
        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(KeyPath))
                foreach (var f in Fields)
                {
                    var v = f.GetValue(this);
                    if (f.FieldType == typeof(bool)) k.SetValue(f.Name, (bool)v ? 1 : 0);
                    else if (f.FieldType == typeof(int)) k.SetValue(f.Name, (int)v);
                    else k.SetValue(f.Name, (string)v ?? "");
                }
        }
        catch { }
    }
}

static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", Name = "Kinetik Feeds";

    public static bool IsOn
    {
        get
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue(Name) != null; }
            catch { return false; }
        }
    }

    public static void Set(bool on)
    {
        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                if (on) k.SetValue(Name, "\"" + Application.ExecutablePath + "\"");
                else k.DeleteValue(Name, false);
        }
        catch { }
    }
}

// Which headlines have been opened, kept in %LOCALAPPDATA%\KinetikFeeds so the dimming survives a restart.
static class ReadList
{
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KinetikFeeds");
    static string File { get { return Path.Combine(Dir, "read.txt"); } }
    static readonly List<string> order = new List<string>();
    static readonly HashSet<string> set = new HashSet<string>();

    public static void Load()
    {
        if (Program.Demo) { Add(DemoData.ReadLinks); return; }
        try { foreach (var l in System.IO.File.ReadAllLines(File)) if (l != "" && set.Add(l)) order.Add(l); } catch { }
    }

    public static bool Has(string link) { return link != null && set.Contains(link); }

    public static void Add(IEnumerable<string> links)
    {
        foreach (var l in links) if (l != null && set.Add(l)) order.Add(l);
        while (order.Count > 3000) { set.Remove(order[0]); order.RemoveAt(0); }
        if (Program.Demo) return;
        try { Directory.CreateDirectory(Dir); System.IO.File.WriteAllLines(File, order); } catch { }
    }

    public static void Clear()
    {
        set.Clear(); order.Clear();
        if (Program.Demo) return;
        try { System.IO.File.Delete(File); } catch { }
    }
}

// ======================================================================= Themes (same set as Kinetik Calendar)
class Theme
{
    public readonly string Name;
    public readonly Color Bg, Text, Sub, Accent;
    public readonly bool Light;

    public Theme(string name, Color bg, Color text, Color sub, Color accent, bool light)
    {
        Name = name; Bg = bg; Text = text; Sub = sub; Accent = accent; Light = light;
    }

    public Color Line { get { return Light ? Color.FromArgb(30, 0, 0, 0) : Color.FromArgb(28, 255, 255, 255); } }

    static Color C(int r, int g, int b) { return Color.FromArgb(r, g, b); }

    public static readonly Theme[] All =
    {
        new Theme("Midnight", C(22, 22, 26), C(242, 242, 246), C(168, 168, 180), C(240, 88, 88), false),
        new Theme("Navy", C(22, 27, 40), C(236, 240, 248), C(138, 150, 176), C(77, 166, 255), false),
        new Theme("Graphite", C(40, 40, 44), C(236, 236, 236), C(160, 160, 166), C(255, 159, 10), false),
        new Theme("Nord", C(46, 52, 64), C(236, 239, 244), C(152, 162, 182), C(136, 192, 208), false),
        new Theme("Dracula", C(40, 42, 54), C(248, 248, 242), C(150, 156, 192), C(255, 121, 198), false),
        new Theme("Solarized", C(0, 43, 54), C(238, 232, 213), C(131, 148, 150), C(203, 75, 22), false),
        new Theme("Forest", C(22, 33, 27), C(228, 240, 231), C(138, 166, 148), C(108, 201, 124), false),
        new Theme("Ocean", C(12, 29, 45), C(228, 242, 250), C(128, 160, 186), C(56, 198, 232), false),
        new Theme("Sunset", C(40, 23, 35), C(250, 236, 240), C(192, 150, 172), C(255, 138, 92), false),
        new Theme("Mono", C(10, 10, 10), C(245, 245, 245), C(140, 140, 140), C(245, 245, 245), false),
        new Theme("Light", C(246, 246, 248), C(24, 24, 28), C(96, 96, 106), C(230, 70, 70), true),
        new Theme("Paper", C(250, 246, 236), C(52, 42, 32), C(132, 116, 96), C(204, 92, 52), true),
        new Theme("Frost", C(236, 242, 250), C(20, 30, 48), C(92, 106, 132), C(40, 120, 230), true),
    };

    public static Theme Get(string name)
    {
        if (name == "Auto")
        {
            bool light = false;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    light = k != null && Convert.ToInt32(k.GetValue("AppsUseLightTheme", 0)) == 1;
            }
            catch { }
            return Get(light ? "Light" : "Midnight");
        }
        foreach (var t in All) if (t.Name == name) return t;
        return All[0];
    }
}

// ======================================================================= Drawing helpers
static class Draw
{
    public static string Family = HasFont("Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";

    public static readonly string[] FontChoices = { "Segoe UI Variable Text", "Segoe UI", "Bahnschrift", "Calibri", "Aptos", "Inter", "Cascadia Code", "Consolas", "Verdana", "Georgia" };

    public static bool HasFont(string name)
    {
        try { using (var f = new FontFamily(name)) return f.Name == name; } catch { return false; }
    }

    public static bool Has(string name) { return HasFont(name); }

    public static void Fill(Graphics g, RectangleF r, float rad, Color c)
    {
        if (c.A == 0 || r.Width <= 0 || r.Height <= 0) return;
        using (var p = Round(r, rad)) using (var b = new SolidBrush(c)) g.FillPath(b, p);
    }

    public static void Stroke(Graphics g, RectangleF r, float rad, Color c, float w)
    {
        using (var p = Round(r, rad)) using (var pen = new Pen(c, w)) g.DrawPath(pen, p);
    }

    public static Color Mix(Color a, Color b, float t)
    {
        t = Math.Max(0, Math.Min(1, t));
        return Color.FromArgb((int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }

    public static GraphicsPath Round(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        rad = Math.Min(rad, Math.Min(r.Width, r.Height) / 2);
        if (rad < 0.5f) { p.AddRectangle(r); return p; }
        float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

// A premultiplied-alpha bitmap backed by a DIB, pushed to a layered window with UpdateLayeredWindow (as in Kinetik).
sealed class DibSurface : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int CX, CY; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLEND { public byte Op, Flags, Alpha, Format; }
    [StructLayout(LayoutKind.Sequential)] struct BITMAPINFOHEADER
    {
        public int Size, Width, Height; public short Planes, BitCount;
        public int Compression, SizeImage, XPels, YPels, ClrUsed, ClrImportant;
    }
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, ref POINT pos, ref SIZE size, IntPtr src, ref POINT srcPos, int key, ref BLEND b, int flags);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

    IntPtr memDC, dib, oldObj;
    Bitmap bmp;
    int w, h;

    public Bitmap Canvas(int width, int height)
    {
        if (dib != IntPtr.Zero && width == w && height == h) return bmp;
        Free();
        memDC = CreateCompatibleDC(IntPtr.Zero);
        var bmi = new BITMAPINFOHEADER { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
        IntPtr bits;
        dib = CreateDIBSection(memDC, ref bmi, 0, out bits, IntPtr.Zero, 0);
        oldObj = SelectObject(memDC, dib);
        bmp = new Bitmap(width, height, width * 4, PixelFormat.Format32bppPArgb, bits);
        w = width; h = height;
        return bmp;
    }

    public void Push(IntPtr hwnd, int x, int y)
    {
        var size = new SIZE { CX = w, CY = h };
        var src = new POINT();
        var pos = new POINT { X = x, Y = y };
        var blend = new BLEND { Op = 0, Flags = 0, Alpha = 255, Format = 1 };
        UpdateLayeredWindow(hwnd, IntPtr.Zero, ref pos, ref size, memDC, ref src, 0, ref blend, 2);
    }

    void Free()
    {
        if (bmp != null) { bmp.Dispose(); bmp = null; }
        if (memDC != IntPtr.Zero) { SelectObject(memDC, oldObj); DeleteDC(memDC); memDC = IntPtr.Zero; }
        if (dib != IntPtr.Zero) { DeleteObject(dib); dib = IntPtr.Zero; }
    }

    public void Dispose() { Free(); }
}

// A card's soft shadow: stacked, slightly larger translucent shapes. Those anti-aliased fills over the whole card cost
// more than the rest of a frame put together, so they're drawn once into a bitmap and copied while nothing about them changes.
sealed class ShadowCache : IDisposable
{
    Bitmap bmp; string key;

    // Lays the shadow into the (cleared) w × h target. look sums up everything draw depends on.
    public void Paint(Graphics target, int w, int h, string look, Action<Graphics> draw)
    {
        var k = w + "x" + h + "|" + look + "|" + (int)target.SmoothingMode + (int)target.PixelOffsetMode;
        if (k != key || bmp == null)
        {
            if (bmp == null || bmp.Width != w || bmp.Height != h) { if (bmp != null) bmp.Dispose(); bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb); }
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = target.SmoothingMode; g.PixelOffsetMode = target.PixelOffsetMode;
                draw(g);
            }
            key = k;
        }
        bmp.SetResolution(target.DpiX, target.DpiY); // so it's copied 1:1
        var mode = target.CompositingMode;
        target.CompositingMode = CompositingMode.SourceCopy;
        target.DrawImageUnscaled(bmp, 0, 0);
        target.CompositingMode = mode;
    }

    public void Dispose() { if (bmp != null) bmp.Dispose(); bmp = null; key = null; }
}

class DarkColors : ProfessionalColorTable
{
    static readonly Color Bg = Color.FromArgb(27, 31, 44), Hi = Color.FromArgb(40, 47, 66), Edge = Color.FromArgb(52, 60, 82);
    public override Color ToolStripDropDownBackground { get { return Bg; } }
    public override Color ImageMarginGradientBegin { get { return Bg; } }
    public override Color ImageMarginGradientMiddle { get { return Bg; } }
    public override Color ImageMarginGradientEnd { get { return Bg; } }
    public override Color MenuBorder { get { return Edge; } }
    public override Color MenuItemBorder { get { return Hi; } }
    public override Color MenuItemSelected { get { return Hi; } }
    public override Color MenuItemSelectedGradientBegin { get { return Hi; } }
    public override Color MenuItemSelectedGradientEnd { get { return Hi; } }
    public override Color MenuItemPressedGradientBegin { get { return Hi; } }
    public override Color MenuItemPressedGradientEnd { get { return Hi; } }
    public override Color SeparatorDark { get { return Edge; } }
    public override Color SeparatorLight { get { return Edge; } }
    public override Color CheckBackground { get { return Color.FromArgb(52, 96, 160); } }
    public override Color CheckSelectedBackground { get { return Color.FromArgb(62, 110, 180); } }
    public override Color CheckPressedBackground { get { return Color.FromArgb(62, 110, 180); } }
}

class DarkMenu : ToolStripProfessionalRenderer
{
    public DarkMenu() : base(new DarkColors()) { RoundedEdges = false; }
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Color.FromArgb(232, 236, 244) : Color.FromArgb(110, 118, 140);
        base.OnRenderItemText(e);
    }
    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = Color.FromArgb(180, 188, 205);
        base.OnRenderArrow(e);
    }
}


// ======================================================================= Fetching and parsing
// ======================================================================= Demo
// Made-up feeds for --demo, so the widget can be shown (and screenshotted) without fetching or showing anything real.
static class DemoData
{
    public static Settings Settings()
    {
        return new Settings
        {
            Layer = 2, ThemeName = Program.DemoTheme ?? "Midnight", MaxItems = 12, X = 240, Y = 160,
            Feeds = "World Desk\tdemo:world\nGadget Weekly\tdemo:tech\nField Notes\tdemo:science\nThe Pitch\tdemo:sport"
        };
    }

    // minutes ago, title
    static readonly Dictionary<string, object[]> Items = new Dictionary<string, object[]>
    {
        { "demo:world", new object[] { 12, "Coastal towns prepare for the biggest king tides in a decade", 95, "Rail link between the two capitals opens two years early", 260, "Parliament passes right-to-repair law for phones and laptops", 600, "Record turnout as voters choose a new city council" } },
        { "demo:tech", new object[] { 25, "Hands-on: the folding e-reader that fits in a jacket pocket", 140, "Why every new laptop this year has a bigger battery", 330, "Open-source maps project passes 10 million contributors", 720, "The best mechanical keyboards under $100" } },
        { "demo:science", new object[] { 48, "Astronomers spot water vapour around a nearby rocky planet", 190, "Coral reef restored in record time with 3D-printed tiles", 480, "How octopuses taste with their arms" } },
        { "demo:sport", new object[] { 70, "Late equaliser keeps the title race open", 230, "Teenager breaks the national 1500 m record", 900, "Season preview: five players to watch" } },
    };

    public static List<Story> Stories(string url, int src)
    {
        var list = new List<Story>();
        object[] a;
        if (!Items.TryGetValue(url, out a)) return list;
        for (int i = 0; i + 1 < a.Length; i += 2)
            list.Add(new Story { Title = (string)a[i + 1], Link = "https://example.com/" + url.Substring(5) + "/" + i, When = DateTime.UtcNow.AddMinutes(-(int)a[i]), Src = src });
        return list;
    }

    // a few already opened, so the dimming of read headlines shows
    public static readonly string[] ReadLinks = { "https://example.com/world/4", "https://example.com/tech/6" };
}

class Story
{
    public string Title, Link;
    public DateTime When; // UTC; MinValue when the feed gives no date
    public int Src;
}

static class Rss
{
    public static readonly Color[] Palette =
    {
        Color.FromArgb(96, 150, 250), Color.FromArgb(250, 104, 96), Color.FromArgb(40, 190, 200), Color.FromArgb(255, 170, 60),
        Color.FromArgb(180, 135, 255), Color.FromArgb(120, 205, 120), Color.FromArgb(255, 120, 190), Color.FromArgb(240, 210, 80),
    };

    static string Get(string url, CancellationToken cancel)
    {
        return KinetikSuite.Http.Text(url, 4 << 20, "KinetikFeeds/" + Program.Version, cancel, 20000,
            "application/rss+xml, application/atom+xml, application/xml, text/xml, text/html;q=0.8");
    }

    static string Child(XmlNode n, params string[] names)
    {
        foreach (XmlNode c in n.ChildNodes)
            if (names.Contains(c.LocalName) && c.InnerText.Trim() != "") return c.InnerText.Trim();
        return null;
    }

    static string Clean(string s)
    {
        s = Regex.Replace(s ?? "", "<[^>]+>", "");
        s = WebUtility.HtmlDecode(s);
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    static DateTime ParseDate(string s)
    {
        if (string.IsNullOrEmpty(s)) return DateTime.MinValue;
        DateTimeOffset d;
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out d)) return d.UtcDateTime;
        // RFC 822 with a zone name, e.g. "Mon, 12 Oct 2026 09:30:00 GMT" or "EST"
        var m = Regex.Match(s, @"(\d{1,2} \w{3} \d{2,4} \d{1,2}:\d{2}(:\d{2})?)\s*([A-Z]{1,4})?");
        if (m.Success)
        {
            DateTime t;
            string[] fmts = { "d MMM yyyy H:mm:ss", "d MMM yyyy H:mm", "d MMM yy H:mm:ss", "d MMM yy H:mm" };
            if (DateTime.TryParseExact(m.Groups[1].Value, fmts, CultureInfo.InvariantCulture, DateTimeStyles.None, out t))
            {
                var zones = new Dictionary<string, int> { { "EST", -5 }, { "EDT", -4 }, { "CST", -6 }, { "CDT", -5 }, { "MST", -7 }, { "MDT", -6 }, { "PST", -8 }, { "PDT", -7 } };
                int off;
                zones.TryGetValue(m.Groups[3].Value, out off);
                return t.AddHours(-off);
            }
        }
        return DateTime.MinValue;
    }

    // Reads RSS 2.0, RSS 1.0 (RDF) and Atom. Returns the feed's own title too.
    public static List<Story> Parse(string xml, int src, out string title)
    {
        var doc = new XmlDocument { XmlResolver = null };
        var rs = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
        using (var r = XmlReader.Create(new System.IO.StringReader(xml), rs)) doc.Load(r);
        var list = new List<Story>();
        title = null;
        var root = doc.DocumentElement;
        if (root == null) return list;
        var channel = root.LocalName == "feed" ? root : root.ChildNodes.Cast<XmlNode>().FirstOrDefault(n => n.LocalName == "channel");
        if (channel != null) title = Clean(Child(channel, "title"));

        var items = new List<XmlNode>();
        Action<XmlNode> collect = null;
        collect = n => { foreach (XmlNode c in n.ChildNodes) { if (c.LocalName == "item" || c.LocalName == "entry") items.Add(c); else if (c.LocalName == "channel") collect(c); } };
        collect(root);

        foreach (var it in items)
        {
            string link = null;
            foreach (XmlNode c in it.ChildNodes)
                if (c.LocalName == "link")
                {
                    var href = c.Attributes != null && c.Attributes["href"] != null ? c.Attributes["href"].Value : c.InnerText.Trim();
                    var rel = c.Attributes != null && c.Attributes["rel"] != null ? c.Attributes["rel"].Value : "alternate";
                    if (href != "" && rel == "alternate") { link = href; break; }
                    if (link == null && href != "") link = href;
                }
            if (link == null) link = Child(it, "guid", "id");
            var t = Clean(Child(it, "title"));
            if (string.IsNullOrEmpty(t)) continue;
            list.Add(new Story { Title = t, Link = link, When = ParseDate(Child(it, "pubDate", "published", "updated", "date")), Src = src });
        }
        return list;
    }

    // Fetches a feed. If the link is a web page rather than a feed, finds the feed it advertises.
    public static List<Story> Fetch(string url, int src, out string title, out string feedUrl)
    { return Fetch(url, src, out title, out feedUrl, CancellationToken.None); }

    public static List<Story> Fetch(string url, int src, out string title, out string feedUrl, CancellationToken cancel)
    {
        feedUrl = url;
        if (url.StartsWith("demo:")) { title = null; return DemoData.Stories(url, src); }
        string body = Get(url, cancel);
        if (Regex.IsMatch(body.Substring(0, Math.Min(body.Length, 2000)), @"<html", RegexOptions.IgnoreCase))
        {
            var m = Regex.Match(body, @"<link[^>]+type=[""']application/(rss|atom)\+xml[""'][^>]*>", RegexOptions.IgnoreCase);
            var h = m.Success ? Regex.Match(m.Value, @"href=[""']([^""']+)[""']", RegexOptions.IgnoreCase) : Match.Empty;
            if (!h.Success) throw new Exception("No feed found on that page");
            feedUrl = new Uri(new Uri(url), WebUtility.HtmlDecode(h.Groups[1].Value)).ToString();
            body = Get(feedUrl, cancel);
        }
        return Parse(body, src, out title);
    }
}

static class UI
{
    public static float S = 1;
    public static int I(float v) { return (int)Math.Round(v * S); }

    public static readonly Color Side = Color.FromArgb(19, 23, 33), Page = Color.FromArgb(24, 28, 40), Card = Color.FromArgb(31, 36, 51),
        CardHi = Color.FromArgb(36, 42, 59), Field = Color.FromArgb(40, 46, 64), Edge = Color.FromArgb(56, 64, 88), Text = Color.FromArgb(232, 236, 244),
        Sub = Color.FromArgb(140, 150, 174), Accent = Color.FromArgb(64, 168, 255), Warn = Color.FromArgb(255, 170, 60), Good = Color.FromArgb(110, 210, 140);

    static readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>();
    public static Font F(float px, FontStyle st = FontStyle.Regular)
    {
        var key = px + "|" + st + "|" + S;
        Font f;
        if (!fonts.TryGetValue(key, out f)) fonts[key] = f = new Font(Draw.Family, px * S, st, GraphicsUnit.Pixel);
        return f;
    }

    public static Button Btn(string text, EventHandler click, bool primary = false)
    {
        var b = new Button
        {
            Text = text, FlatStyle = FlatStyle.Flat, BackColor = primary ? Color.FromArgb(40, 120, 210) : Field, ForeColor = Text,
            Font = F(13), AutoSize = true, MinimumSize = new Size(I(84), I(32)), Padding = new Padding(I(8), 0, I(8), 0), UseVisualStyleBackColor = false, Cursor = Cursors.Hand,
        };
        b.FlatAppearance.BorderColor = primary ? Color.FromArgb(60, 140, 230) : Edge;
        b.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(52, 134, 228) : Color.FromArgb(50, 57, 78);
        b.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(34, 104, 190) : Color.FromArgb(44, 50, 70);
        b.Click += click;
        return b;
    }

    // A text box inside a rounded field with a focus ring, matching the rest of the window.
    public static Panel TextField(TextBox t, int w)
    {
        var p = new Panel { Size = new Size(w, I(34)), BackColor = Card };
        p.Paint += (s, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(p.BackColor);
            Draw.Fill(e.Graphics, new RectangleF(0, 0, p.Width - 1, p.Height - 1), I(7), UI.Field);
            Draw.Stroke(e.Graphics, new RectangleF(0.5f, 0.5f, p.Width - 2, p.Height - 2), I(7), t.Focused ? Accent : Edge, 1);
        };
        t.BorderStyle = BorderStyle.None; t.BackColor = UI.Field; t.ForeColor = Text; t.Font = F(13.5f);
        t.SetBounds(I(10), (p.Height - t.PreferredHeight) / 2, w - I(20), t.PreferredHeight);
        t.GotFocus += (s, e) => p.Invalidate();
        t.LostFocus += (s, e) => p.Invalidate();
        p.Controls.Add(t);
        return p;
    }

    // Small line icons, drawn rather than loaded so they stay crisp at any scale.
    public static void Glyph(Graphics g, string name, RectangleF r, Color c)
    {
        float x = r.X, y = r.Y, s = r.Width;
        Func<float, float, PointF> P = (a, b) => new PointF(x + a * s, y + b * s);
        Func<float, float, float, float, RectangleF> R = (a, b, w, h) => new RectangleF(x + a * s, y + b * s, w * s, h * s);
        var sm = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var p = new Pen(c, Math.Max(1.4f, s / 11)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        using (var b = new SolidBrush(c))
        {
            switch (name)
            {
                case "calendar":
                    using (var path = Draw.Round(R(0.12f, 0.2f, 0.76f, 0.68f), s * 0.12f)) g.DrawPath(p, path);
                    g.DrawLine(p, P(0.12f, 0.42f), P(0.88f, 0.42f));
                    g.DrawLine(p, P(0.34f, 0.1f), P(0.34f, 0.28f)); g.DrawLine(p, P(0.66f, 0.1f), P(0.66f, 0.28f));
                    break;
                case "list":
                    for (int i = 0; i < 3; i++)
                    {
                        float yy = 0.27f + 0.23f * i;
                        g.FillEllipse(b, R(0.12f, yy - 0.06f, 0.12f, 0.12f));
                        g.DrawLine(p, P(0.36f, yy), P(0.88f, yy));
                    }
                    break;
                case "palette":
                    g.DrawEllipse(p, R(0.12f, 0.12f, 0.76f, 0.76f));
                    g.FillEllipse(b, R(0.28f, 0.3f, 0.14f, 0.14f)); g.FillEllipse(b, R(0.52f, 0.24f, 0.14f, 0.14f)); g.FillEllipse(b, R(0.6f, 0.5f, 0.14f, 0.14f));
                    break;
                case "layout":
                    using (var path = Draw.Round(R(0.12f, 0.14f, 0.76f, 0.72f), s * 0.1f)) g.DrawPath(p, path);
                    g.DrawLine(p, P(0.12f, 0.38f), P(0.88f, 0.38f)); g.DrawLine(p, P(0.42f, 0.38f), P(0.42f, 0.86f));
                    break;
                case "gear":
                    g.DrawEllipse(p, R(0.33f, 0.33f, 0.34f, 0.34f));
                    for (int i = 0; i < 8; i++)
                    {
                        double a = i * Math.PI / 4;
                        g.DrawLine(p, P(0.5f + 0.27f * (float)Math.Cos(a), 0.5f + 0.27f * (float)Math.Sin(a)), P(0.5f + 0.4f * (float)Math.Cos(a), 0.5f + 0.4f * (float)Math.Sin(a)));
                    }
                    break;
                case "info":
                    g.DrawEllipse(p, R(0.12f, 0.12f, 0.76f, 0.76f));
                    g.DrawLine(p, P(0.5f, 0.46f), P(0.5f, 0.68f)); g.FillEllipse(b, R(0.45f, 0.27f, 0.1f, 0.1f));
                    break;
                case "layers":
                    g.DrawPolygon(p, new[] { P(0.5f, 0.15f), P(0.88f, 0.36f), P(0.5f, 0.57f), P(0.12f, 0.36f) });
                    g.DrawLines(p, new[] { P(0.12f, 0.56f), P(0.5f, 0.77f), P(0.88f, 0.56f) });
                    break;
                case "lock":
                    using (var path = Draw.Round(R(0.2f, 0.44f, 0.6f, 0.44f), s * 0.08f)) g.DrawPath(p, path);
                    g.DrawArc(p, R(0.32f, 0.14f, 0.36f, 0.5f), 180, 180);
                    break;
                case "pointer":
                    g.DrawPolygon(p, new[] { P(0.26f, 0.12f), P(0.26f, 0.8f), P(0.42f, 0.65f), P(0.54f, 0.88f), P(0.64f, 0.83f), P(0.52f, 0.6f), P(0.74f, 0.6f) });
                    break;
                case "magnet":
                    g.DrawArc(p, R(0.2f, 0.3f, 0.6f, 0.56f), 0, 180);
                    g.DrawLine(p, P(0.2f, 0.58f), P(0.2f, 0.16f)); g.DrawLine(p, P(0.8f, 0.58f), P(0.8f, 0.16f));
                    g.DrawLine(p, P(0.12f, 0.3f), P(0.28f, 0.3f)); g.DrawLine(p, P(0.72f, 0.3f), P(0.88f, 0.3f));
                    break;
                case "clock":
                    g.DrawEllipse(p, R(0.12f, 0.12f, 0.76f, 0.76f));
                    g.DrawLine(p, P(0.5f, 0.5f), P(0.5f, 0.28f)); g.DrawLine(p, P(0.5f, 0.5f), P(0.66f, 0.58f));
                    break;
                case "sync":
                    g.DrawArc(p, R(0.18f, 0.18f, 0.64f, 0.64f), -60, 290);
                    g.DrawLines(p, new[] { P(0.62f, 0.12f), P(0.68f, 0.24f), P(0.56f, 0.28f) });
                    break;
                case "eye":
                    g.DrawArc(p, R(0.08f, 0.28f, 0.84f, 0.8f), 200, 140);
                    g.DrawArc(p, R(0.08f, -0.08f, 0.84f, 0.8f), 20, 140);
                    g.DrawEllipse(p, R(0.38f, 0.38f, 0.24f, 0.24f));
                    break;
                case "text":
                    g.DrawLine(p, P(0.2f, 0.22f), P(0.8f, 0.22f)); g.DrawLine(p, P(0.5f, 0.22f), P(0.5f, 0.84f));
                    break;
                case "size":
                    g.DrawLine(p, P(0.22f, 0.78f), P(0.78f, 0.22f));
                    g.DrawLines(p, new[] { P(0.5f, 0.22f), P(0.78f, 0.22f), P(0.78f, 0.5f) });
                    g.DrawLines(p, new[] { P(0.22f, 0.5f), P(0.22f, 0.78f), P(0.5f, 0.78f) });
                    break;
                case "width":
                    g.DrawLine(p, P(0.12f, 0.5f), P(0.88f, 0.5f));
                    g.DrawLines(p, new[] { P(0.26f, 0.36f), P(0.12f, 0.5f), P(0.26f, 0.64f) });
                    g.DrawLines(p, new[] { P(0.74f, 0.36f), P(0.88f, 0.5f), P(0.74f, 0.64f) });
                    break;
                case "height":
                    g.DrawLine(p, P(0.5f, 0.12f), P(0.5f, 0.88f));
                    g.DrawLines(p, new[] { P(0.36f, 0.26f), P(0.5f, 0.12f), P(0.64f, 0.26f) });
                    g.DrawLines(p, new[] { P(0.36f, 0.74f), P(0.5f, 0.88f), P(0.64f, 0.74f) });
                    break;
                case "round":
                    g.DrawLine(p, P(0.2f, 0.86f), P(0.2f, 0.52f)); g.DrawArc(p, R(0.2f, 0.2f, 0.64f, 0.64f), 180, 90); g.DrawLine(p, P(0.52f, 0.2f), P(0.86f, 0.2f));
                    break;
                case "opacity":
                    g.DrawEllipse(p, R(0.14f, 0.14f, 0.72f, 0.72f)); g.FillPie(b, x + 0.14f * s, y + 0.14f * s, 0.72f * s, 0.72f * s, 90, 180);
                    break;
                case "shadow":
                    using (var sb = new SolidBrush(Color.FromArgb(90, c))) g.FillRectangle(sb, R(0.3f, 0.3f, 0.58f, 0.58f));
                    g.DrawRectangle(p, x + 0.12f * s, y + 0.12f * s, 0.58f * s, 0.58f * s);
                    break;
                case "border":
                    using (var path = Draw.Round(R(0.14f, 0.14f, 0.72f, 0.72f), s * 0.16f)) g.DrawPath(p, path);
                    break;
                case "grid":
                    using (var path = Draw.Round(R(0.12f, 0.12f, 0.76f, 0.76f), s * 0.1f)) g.DrawPath(p, path);
                    g.DrawLine(p, P(0.12f, 0.5f), P(0.88f, 0.5f)); g.DrawLine(p, P(0.5f, 0.12f), P(0.5f, 0.88f));
                    break;
                case "hash":
                    g.DrawLine(p, P(0.38f, 0.14f), P(0.32f, 0.86f)); g.DrawLine(p, P(0.68f, 0.14f), P(0.62f, 0.86f));
                    g.DrawLine(p, P(0.16f, 0.36f), P(0.86f, 0.36f)); g.DrawLine(p, P(0.14f, 0.64f), P(0.84f, 0.64f));
                    break;
                case "density":
                    g.DrawLine(p, P(0.14f, 0.26f), P(0.86f, 0.26f)); g.DrawLine(p, P(0.14f, 0.5f), P(0.86f, 0.5f)); g.DrawLine(p, P(0.14f, 0.74f), P(0.86f, 0.74f));
                    break;
                case "marker":
                    g.DrawLine(p, P(0.18f, 0.18f), P(0.18f, 0.82f)); g.DrawLine(p, P(0.4f, 0.34f), P(0.86f, 0.34f)); g.DrawLine(p, P(0.4f, 0.62f), P(0.74f, 0.62f));
                    break;
                case "bolt":
                    g.DrawPolygon(p, new[] { P(0.56f, 0.1f), P(0.24f, 0.56f), P(0.48f, 0.56f), P(0.42f, 0.9f), P(0.76f, 0.42f), P(0.52f, 0.42f) });
                    break;
                case "power":
                    g.DrawArc(p, R(0.16f, 0.2f, 0.68f, 0.68f), -60, 300); g.DrawLine(p, P(0.5f, 0.1f), P(0.5f, 0.48f));
                    break;
                case "drop":
                    g.DrawEllipse(p, R(0.2f, 0.34f, 0.6f, 0.54f)); g.DrawLines(p, new[] { P(0.23f, 0.52f), P(0.5f, 0.1f), P(0.77f, 0.52f) });
                    break;
                case "plus":
                    g.DrawLine(p, P(0.5f, 0.18f), P(0.5f, 0.82f)); g.DrawLine(p, P(0.18f, 0.5f), P(0.82f, 0.5f));
                    break;
                case "folder":
                    g.DrawPolygon(p, new[] { P(0.12f, 0.24f), P(0.4f, 0.24f), P(0.48f, 0.34f), P(0.88f, 0.34f), P(0.88f, 0.8f), P(0.12f, 0.8f) });
                    break;
                case "memory":
                    g.DrawRectangle(p, x + 0.12f * s, y + 0.3f * s, 0.76f * s, 0.4f * s);
                    for (int i = 0; i < 4; i++) g.DrawLine(p, P(0.24f + i * 0.17f, 0.7f), P(0.24f + i * 0.17f, 0.84f));
                    break;
                case "rss":
                    g.FillEllipse(b, R(0.14f, 0.7f, 0.16f, 0.16f));
                    g.DrawArc(p, R(-0.24f, 0.18f, 0.9f, 0.9f), 270, 90);
                    g.DrawArc(p, R(-0.5f, -0.06f, 1.38f, 1.38f), 270, 90);
                    break;
                case "news":
                    using (var path = Draw.Round(R(0.12f, 0.16f, 0.76f, 0.68f), s * 0.08f)) g.DrawPath(p, path);
                    g.DrawLine(p, P(0.26f, 0.34f), P(0.74f, 0.34f)); g.DrawLine(p, P(0.26f, 0.5f), P(0.74f, 0.5f)); g.DrawLine(p, P(0.26f, 0.66f), P(0.56f, 0.66f));
                    break;
                default:
                    g.DrawEllipse(p, R(0.25f, 0.25f, 0.5f, 0.5f));
                    break;
            }
        }
        g.SmoothingMode = sm;
    }
}

// An on/off switch with a sliding knob.
class Toggle : Control
{
    bool on;
    float pos;
    readonly System.Windows.Forms.Timer t = new System.Windows.Forms.Timer { Interval = 15 };
    public event EventHandler Changed;

    public Toggle(bool value)
    {
        on = value; pos = value ? 1 : 0;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Size = new Size(UI.I(42), UI.I(22));
        Cursor = Cursors.Hand;
        BackColor = UI.Card;
        t.Tick += (s, e) =>
        {
            float target = on ? 1 : 0;
            pos += (target - pos) * 0.35f;
            if (Math.Abs(target - pos) < 0.02f) { pos = target; t.Stop(); }
            Invalidate();
        };
    }

    public bool Checked
    {
        get { return on; }
        set
        {
            if (on == value) return;
            on = value; t.Start();
            if (Changed != null) Changed(this, EventArgs.Empty);
        }
    }

    protected override void OnClick(EventArgs e) { Focus(); Checked = !Checked; base.OnClick(e); }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) Checked = !Checked; base.OnKeyDown(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void Dispose(bool disposing) { if (disposing) t.Dispose(); base.Dispose(disposing); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(1, 1, Width - 2, Height - 2);
        Draw.Fill(g, r, r.Height / 2, Draw.Mix(Color.FromArgb(52, 58, 80), UI.Accent, pos));
        if (pos < 1) Draw.Stroke(g, r, r.Height / 2, Color.FromArgb((int)(255 * (1 - pos)), 82, 92, 120), 1);
        if (Focused) Draw.Stroke(g, RectangleF.Inflate(r, -0.5f, -0.5f), r.Height / 2, Color.FromArgb(120, 255, 255, 255), 1);
        float d = Height - UI.I(8), x = UI.I(4) + pos * (Width - UI.I(8) - d);
        using (var b = new SolidBrush(Draw.Mix(Color.FromArgb(200, 206, 222), Color.White, pos))) g.FillEllipse(b, x, UI.I(4), d, d);
    }
}

// A slider with its value shown alongside. Click it first to use the mouse wheel or arrow keys, as in Kinetik.
class Slider : Control
{
    public int Min, Max, Value;
    public Func<int, string> Format = v => v.ToString();
    public event EventHandler Changed;
    bool drag;

    public Slider(int min, int max, int value)
    {
        Min = min; Max = max; Value = Math.Max(min, Math.Min(max, value));
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Size = new Size(UI.I(230), UI.I(26));
        BackColor = UI.Card;
        Cursor = Cursors.Hand;
    }

    RectangleF Track { get { return new RectangleF(UI.I(8), Height / 2f - UI.I(2), Width - UI.I(70), UI.I(4)); } }

    void Set(int v)
    {
        v = Math.Max(Min, Math.Min(Max, v));
        if (v == Value) return;
        Value = v; Invalidate();
        if (Changed != null) Changed(this, EventArgs.Empty);
    }

    void FromMouse(int mx)
    {
        var tr = Track;
        Set(Min + (int)Math.Round((mx - tr.X) / tr.Width * (Max - Min)));
    }

    int Step { get { return Max - Min > 100 ? 5 : 1; } }

    protected override void OnMouseDown(MouseEventArgs e) { Focus(); if (!OnNumber(e.X)) { drag = true; FromMouse(e.X); } base.OnMouseDown(e); }
    protected override void OnMouseMove(MouseEventArgs e) { if (drag) FromMouse(e.X); else Cursor = OnNumber(e.X) ? Cursors.IBeam : Cursors.Hand; base.OnMouseMove(e); }
    protected override void OnMouseUp(MouseEventArgs e) { drag = false; base.OnMouseUp(e); }
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (Focused) { Set(Value + Math.Sign(e.Delta) * Step); ((HandledMouseEventArgs)e).Handled = true; }
        else base.OnMouseWheel(e);
    }
    protected override bool IsInputKey(Keys k) { return k == Keys.Left || k == Keys.Right || base.IsInputKey(k); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Left) Set(Value - Step);
        else if (e.KeyCode == Keys.Right) Set(Value + Step);
        base.OnKeyDown(e);
    }
    // Double-click the number, press Enter or just start typing to enter an exact value
    TextBox edit;
    bool OnNumber(int x) { return x > Track.Right + UI.I(4); }

    void BeginEdit(string seed)
    {
        if (edit != null) return;
        edit = new TextBox { BorderStyle = BorderStyle.FixedSingle, TextAlign = HorizontalAlignment.Right, Font = UI.F(11f), BackColor = Color.FromArgb(36, 40, 56), ForeColor = UI.Text, Width = UI.I(60) };
        edit.Location = new Point(Width - edit.Width - 1, (Height - edit.Height) / 2);
        edit.Text = seed ?? Value.ToString();
        edit.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; EndEdit(e.KeyCode == Keys.Enter); Focus(); }
            else if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down)
            {
                e.SuppressKeyPress = true;
                int cur; if (!int.TryParse(edit.Text.Trim(), out cur)) cur = Value;
                edit.Text = Math.Max(Min, Math.Min(Max, cur + (e.KeyCode == Keys.Up ? 1 : -1))).ToString();
                edit.SelectionStart = edit.Text.Length;
            }
        };
        edit.LostFocus += (s, e) => EndEdit(true);
        Controls.Add(edit);
        edit.Focus();
        if (seed == null) edit.SelectAll(); else edit.SelectionStart = edit.Text.Length;
    }

    void EndEdit(bool commit)
    {
        if (edit == null) return;
        var t = edit; edit = null;
        var digits = new StringBuilder();
        foreach (char ch in t.Text) if (char.IsDigit(ch) || (ch == '-' && digits.Length == 0)) digits.Append(ch);
        int v;
        if (commit && int.TryParse(digits.ToString(), out v)) Set(v);
        Controls.Remove(t); t.Dispose(); Invalidate();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e) { if (OnNumber(e.X)) BeginEdit(null); base.OnMouseDoubleClick(e); }
    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        if (char.IsDigit(e.KeyChar) || e.KeyChar == '-') { e.Handled = true; BeginEdit(e.KeyChar.ToString()); }
        else if (e.KeyChar == (char)13) { e.Handled = true; BeginEdit(null); }
        base.OnKeyPress(e);
    }

    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        var tr = Track;
        float f = Max == Min ? 0 : (Value - Min) / (float)(Max - Min);
        Draw.Fill(g, tr, tr.Height / 2, Color.FromArgb(52, 58, 80));
        Draw.Fill(g, new RectangleF(tr.X, tr.Y, Math.Max(tr.Height, tr.Width * f), tr.Height), tr.Height / 2, UI.Accent);
        float d = UI.I(14), kx = tr.X + tr.Width * f - d / 2;
        if (Focused) using (var b = new SolidBrush(Color.FromArgb(60, UI.Accent))) g.FillEllipse(b, kx - UI.I(4), Height / 2f - d / 2 - UI.I(4), d + UI.I(8), d + UI.I(8));
        using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, kx, Height / 2f - d / 2, d, d);
        TextRenderer.DrawText(g, Format(Value), UI.F(12.5f), new Rectangle((int)tr.Right + UI.I(10), 0, Width - (int)tr.Right - UI.I(10), Height), UI.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
    }
}

class DarkCombo : ComboBox
{
    public DarkCombo(string[] items, int sel)
    {
        DropDownStyle = ComboBoxStyle.DropDownList;
        DrawMode = DrawMode.OwnerDrawFixed;
        FlatStyle = FlatStyle.Flat;
        BackColor = UI.Field; ForeColor = UI.Text;
        Font = UI.F(13);
        ItemHeight = UI.I(24);
        Width = UI.I(210);
        Items.AddRange(items);
        SelectedIndex = Math.Max(0, Math.Min(items.Length - 1, sel));
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        bool edit = (e.State & DrawItemState.ComboBoxEdit) != 0, sel = (e.State & DrawItemState.Selected) != 0 && !edit;
        using (var b = new SolidBrush(sel ? Color.FromArgb(46, 96, 168) : UI.Field)) e.Graphics.FillRectangle(b, e.Bounds);
        TextRenderer.DrawText(e.Graphics, Items[e.Index].ToString(), Font, new Rectangle(e.Bounds.X + UI.I(6), e.Bounds.Y, e.Bounds.Width - UI.I(6), e.Bounds.Height), UI.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
    }
}

// A rounded settings row: icon (or colour dot), title, description, and a control on the right.
class Card : Control
{
    public string Glyph, Title, Sub;
    public Color? Swatch;
    public Color SubColor = UI.Sub;
    readonly Control right;

    public Card(string glyph, string title, string sub, Control right, int width)
    {
        Glyph = glyph; Title = title; Sub = sub; this.right = right;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = UI.Page;
        Size = new Size(width, UI.I(sub == null ? 50 : 60));
        Margin = new Padding(0, 0, 0, UI.I(8));
        if (right != null) { Controls.Add(right); SetBackColors(right); }
    }

    static void SetBackColors(Control c)
    {
        if (c is Toggle || c is Slider || c is FlowLayoutPanel || c is Label || c is Swatch || c.GetType() == typeof(Panel)) c.BackColor = UI.Card;
        foreach (Control k in c.Controls) SetBackColors(k);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        if (right != null) right.Location = new Point(Width - right.Width - UI.I(14), (Height - right.Height) / 2);
        base.OnLayout(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        Draw.Fill(g, new RectangleF(0, 0, Width - 1, Height - 1), UI.I(8), UI.Card);
        float isz = UI.I(18), ix = UI.I(16), iy = (Height - isz) / 2f;
        if (Swatch.HasValue) using (var b = new SolidBrush(Swatch.Value)) g.FillEllipse(b, ix + UI.I(2), iy + UI.I(2), isz - UI.I(4), isz - UI.I(4));
        else if (Glyph != null) UI.Glyph(g, Glyph, new RectangleF(ix, iy, isz, isz), UI.Accent);
        int tx = UI.I(50), tw = (right != null ? right.Left - UI.I(12) : Width - UI.I(14)) - tx;
        if (Sub == null)
            TextRenderer.DrawText(g, Title, UI.F(13.5f), new Rectangle(tx, 0, tw, Height), UI.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        else
        {
            TextRenderer.DrawText(g, Title, UI.F(13.5f), new Rectangle(tx, Height / 2 - UI.I(19), tw, UI.I(20)), UI.Text, TextFormatFlags.Bottom | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, Sub, UI.F(11.5f), new Rectangle(tx, Height / 2 + UI.I(1), tw, UI.I(18)), SubColor, TextFormatFlags.Top | TextFormatFlags.EndEllipsis);
        }
    }
}

class Swatch : Control
{
    public Color Color { get; set; }
    public bool Selected;
    bool hot;

    public Swatch(Color c, int size)
    {
        Color = c;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        Size = new Size(size, size);
        Cursor = Cursors.Hand;
        BackColor = UI.Card;
    }

    protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hot = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float inset = Selected || hot ? UI.I(4) : UI.I(2);
        Draw.Fill(g, new RectangleF(inset, inset, Width - inset * 2, Height - inset * 2), Width, Color);
        if (Selected) Draw.Stroke(g, new RectangleF(1, 1, Width - 2.5f, Height - 2.5f), Width, Color.White, 1.5f);
        else if (hot) Draw.Stroke(g, new RectangleF(1, 1, Width - 2.5f, Height - 2.5f), Width, Color.FromArgb(120, 255, 255, 255), 1);
    }
}

// A theme in the gallery, drawn as a tiny widget.
class ThemeTile : Control
{
    public readonly string ThemeName;
    public bool Selected;
    bool hot;
    float sel;
    readonly System.Windows.Forms.Timer t = new System.Windows.Forms.Timer { Interval = 15 };

    public ThemeTile(string name, bool selected)
    {
        ThemeName = name; Selected = selected; sel = selected ? 1 : 0;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        Size = new Size(UI.I(110), UI.I(84));
        Margin = new Padding(0, 0, UI.I(8), UI.I(8));
        Cursor = Cursors.Hand;
        BackColor = UI.Page;
        t.Tick += (s, e) =>
        {
            float target = Selected ? 1 : 0;
            sel += (target - sel) * 0.3f;
            if (Math.Abs(target - sel) < 0.02f) { sel = target; t.Stop(); }
            Invalidate();
        };
    }

    public void SetSelected(bool v) { if (Selected == v) return; Selected = v; t.Start(); }
    protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hot = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void Dispose(bool disposing) { if (disposing) t.Dispose(); base.Dispose(disposing); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var outer = new RectangleF(1, 1, Width - 3, Height - 3);
        Draw.Fill(g, outer, UI.I(10), hot ? UI.CardHi : UI.Card);
        if (sel > 0) Draw.Stroke(g, outer, UI.I(10), Color.FromArgb((int)(255 * sel), UI.Accent), 2);
        var inner = new RectangleF(UI.I(8), UI.I(8), Width - UI.I(16), Height - UI.I(34));
        if (ThemeName == "Auto")
        {
            Mini(g, inner, Theme.Get("Midnight"));
            var st = g.Save();
            g.SetClip(new RectangleF(inner.X + inner.Width / 2, inner.Y, inner.Width, inner.Height));
            Mini(g, inner, Theme.Get("Light"));
            g.Restore(st);
        }
        else Mini(g, inner, Theme.Get(ThemeName));
        TextRenderer.DrawText(g, ThemeName == "Auto" ? "Match Windows" : ThemeName, UI.F(12), new Rectangle(0, Height - UI.I(26), Width, UI.I(20)), Selected ? UI.Text : UI.Sub,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    static void Mini(Graphics g, RectangleF r, Theme th)
    {
        Draw.Fill(g, r, UI.I(6), th.Bg);
        float x = r.X + UI.I(7), w = r.Width - UI.I(14), y = r.Y + UI.I(7);
        using (var b = new SolidBrush(th.Accent)) g.FillEllipse(b, x, y, UI.I(7), UI.I(7));
        Draw.Fill(g, new RectangleF(x + UI.I(11), y + UI.I(1), w * 0.45f, UI.I(4)), UI.I(2), th.Text);
        for (int i = 0; i < 3; i++)
        {
            float yy = y + UI.I(13) + i * UI.I(9);
            Draw.Fill(g, new RectangleF(x, yy, UI.I(3), UI.I(6)), UI.I(1.5f), Rss.Palette[i]);
            Draw.Fill(g, new RectangleF(x + UI.I(7), yy + UI.I(1), w * (0.62f - i * 0.12f), UI.I(4)), UI.I(2), Color.FromArgb(150, th.Sub));
            Draw.Fill(g, new RectangleF(x + w - UI.I(14), yy + UI.I(1), UI.I(14), UI.I(4)), UI.I(2), Color.FromArgb(200, th.Text));
        }
    }
}

class NavItem : Control
{
    public readonly string Page, Glyph;
    public bool Selected;
    bool hot;

    public NavItem(string page, string glyph)
    {
        Page = page; Glyph = glyph;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        Size = new Size(UI.I(164), UI.I(40));
        Cursor = Cursors.Hand;
        BackColor = UI.Side;
    }

    protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hot = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (Selected || hot) Draw.Fill(g, new RectangleF(UI.I(6), UI.I(2), Width - UI.I(12), Height - UI.I(4)), UI.I(7), Selected ? Color.FromArgb(33, 39, 56) : Color.FromArgb(26, 31, 45));
        if (Selected) Draw.Fill(g, new RectangleF(UI.I(6), Height / 2f - UI.I(8), UI.I(3), UI.I(16)), UI.I(1.5f), UI.Accent);
        UI.Glyph(g, Glyph, new RectangleF(UI.I(20), (Height - UI.I(18)) / 2f, UI.I(18), UI.I(18)), Selected ? UI.Accent : UI.Sub);
        TextRenderer.DrawText(g, Page, UI.F(13.5f), new Rectangle(UI.I(50), 0, Width - UI.I(50), Height), Selected ? UI.Text : Color.FromArgb(190, 198, 215), TextFormatFlags.VerticalCenter);
    }
}

// ======================================================================= The widget
class FeedsForm : Form
{
    [StructLayout(LayoutKind.Sequential)] struct WINDOWPOS { public IntPtr hwnd, hwndInsertAfter; public int x, y, cx, cy; public uint flags; }
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);

    public readonly Settings Cfg = Settings.Load();
    Settings c { get { return Cfg; } }
    readonly DibSurface surface = new DibSurface();
    readonly System.Windows.Forms.Timer refresh = new System.Windows.Forms.Timer(), minute = new System.Windows.Forms.Timer { Interval = 60000 };
    readonly System.Windows.Forms.Timer spin = new System.Windows.Forms.Timer { Interval = 40 }; // turns the refresh spinner
    readonly NotifyIcon tray = new NotifyIcon();
    readonly ContextMenuStrip menu = KinetikSuite.MenuFocus.Attach(new ContextMenuStrip());
    public Icon AppIcon;
    List<Story> stories = new List<Story>();
    public readonly Dictionary<int, string> Errors = new Dictionary<int, string>();
    public readonly Dictionary<int, int> Counts = new Dictionary<int, int>();
    public DateTime FetchedAt = DateTime.MinValue;
    public volatile bool Busy;
    CancellationTokenSource request;
    int layer, hover = -1;
    float scroll;
    Frame last = new Frame();
    bool dragging, moved, openSettings;
    Point grab, downAt;
    SettingsForm settings;

    public FeedsForm(bool openSettings)
    {
        this.openSettings = openSettings;
        layer = c.Layer;
        ReadList.Load();
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Text = "Kinetik Feeds";
        try { AppIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { AppIcon = SystemIcons.Application; }
        menu.Opening += (s, e) => BuildMenu();
        tray.Icon = AppIcon;
        tray.Text = Program.Demo ? "Kinetik Feeds (demo)" : "Kinetik Feeds";
        Backup.BeforeExit = () => tray.Visible = false;
        tray.ContextMenuStrip = menu;
        tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) OpenSettings(null); };
        tray.Visible = true;
        refresh.Tick += (s, e) => FetchAll();
        spin.Tick += (s, e) => { if (Busy) Render(); else spin.Stop(); };
        minute.Tick += (s, e) => Render(); // keeps "5m ago" current
        minute.Start();
        SystemEvents.PowerModeChanged += OnPower;
    }

    void OnPower(object sender, PowerModeChangedEventArgs e) { if (e.Mode == PowerModes.Resume) try { BeginInvoke((Action)FetchAll); } catch { } }

    public void Shutdown()
    { if (settings != null && !settings.IsDisposed) settings.Close(); c.Save(); Close(); }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80000 | 0x80 | 0x08000000; // LAYERED | TOOLWINDOW | NOACTIVATE
            if (layer == 2) cp.ExStyle |= 0x8;          // TOPMOST
            return cp;
        }
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x21) { m.Result = (IntPtr)3; return; } // WM_MOUSEACTIVATE → MA_NOACTIVATE
        if (m.Msg == 0x46 && layer == 0) // WM_WINDOWPOSCHANGING: "on the desktop" keeps it underneath every other window
        {
            var wp = (WINDOWPOS)Marshal.PtrToStructure(m.LParam, typeof(WINDOWPOS));
            if ((wp.flags & 0x4) == 0) { wp.hwndInsertAfter = new IntPtr(1); Marshal.StructureToPtr(wp, m.LParam, false); }
        }
        base.WndProc(ref m);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Render();
        if (FetchedAt == DateTime.MinValue && !Busy) FetchAll();
        if (openSettings) { openSettings = false; BeginInvoke((Action)(() => OpenSettings(null))); }
    }

    protected override void SetVisibleCore(bool value)
    {
        if (!IsHandleCreated) CreateHandle();
        base.SetVisibleCore(false); // shown by Render() with SW_SHOWNA, so it never steals focus
    }

    protected override void Dispose(bool disposing)
    {
        surface.Dispose();
        if (disposing) { generation++; if (request != null) { request.Cancel(); request = null; } SystemEvents.PowerModeChanged -= OnPower; spin.Dispose(); refresh.Dispose(); minute.Dispose(); tray.Dispose(); menu.Dispose(); shadow.Dispose(); }
        base.Dispose(disposing);
    }

    // ---- settings
    public void OpenSettings(string page)
    {
        if (settings == null || settings.IsDisposed)
        {
            settings = new SettingsForm(this, page ?? "Feeds");
            settings.FormClosed += (s, e) => { settings = null; c.Save(); };
            settings.Show();
        }
        else
        {
            if (page != null) settings.ShowPage(page);
            if (settings.WindowState == FormWindowState.Minimized) settings.WindowState = FormWindowState.Normal;
            settings.Activate();
        }
    }

    // Called after any change in Settings.
    public void Apply()
    {
        c.Save();
        Render();
        if (settings != null && !settings.IsDisposed) settings.PreviewChanged();
    }

    public void RecreateWidget()
    {
        layer = c.Layer;
        RecreateHandle();
        Render();
    }

    public void FeedsChanged()
    {
        c.Save();
        stories = new List<Story>();
        Errors.Clear(); Counts.Clear();
        Render();
        FetchAll(true);
    }

    // ---- fetching: every feed on its own background thread, merged newest first
    public void FetchAll() { FetchAll(false); }

    void FetchAll(bool force)
    {
        if (Busy && !force) return;
        if (request != null) request.Cancel();
        var cancel = new CancellationTokenSource(); request = cancel;
        cancel.CancelAfter(20000); var token = cancel.Token;
        Busy = true; int gen = ++generation;
        refresh.Stop(); if (c.ShowHeader) spin.Start(); Render();
        var sources = c.List.Select(source => new Source { Name = source.Name, Url = source.Url, On = source.On, Color = source.Color }).ToList();
        ThreadPool.QueueUserWorkItem(_ =>
        {
            var all = new List<Story>(); var errs = new Dictionary<int, string>(); var counts = new Dictionary<int, int>(); var gate = new object();
            try
            {
                System.Threading.Tasks.Parallel.For(0, sources.Count,
                    new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token }, i =>
                {
                    var source = sources[i]; if (!source.On) return;
                    try
                    {
                        string title, url;
                        var got = Rss.Fetch(source.Url, i, out title, out url, token);
                        lock (gate) { all.AddRange(got.Take(60)); counts[i] = got.Count; }
                    }
                    catch (Exception ex) { lock (gate) errs[i] = token.IsCancellationRequested ? "Refresh timed out" : Friendly(ex); }
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { lock (gate) errs[-1] = Friendly(ex); }
            for (int i = 0; i < sources.Count; i++) if (sources[i].On && !counts.ContainsKey(i) && !errs.ContainsKey(i)) errs[i] = "Refresh timed out";
            var merged = all.GroupBy(story => story.Link ?? story.Title).Select(group => group.First()).OrderByDescending(story => story.When).ToList();
            try { BeginInvoke((Action)(() =>
            {
                try
                {
                    if (gen != generation || IsDisposed) return;
                    request = null; Errors.Clear(); Counts.Clear();
                    foreach (var pair in errs) Errors[pair.Key] = pair.Value;
                    foreach (var pair in counts) Counts[pair.Key] = pair.Value;
                    // Retain an already displayed feed when that source is offline.
                    stories = merged.Concat(stories.Where(story => errs.ContainsKey(story.Src)))
                        .GroupBy(story => story.Link ?? story.Title).Select(group => group.First()).OrderByDescending(story => story.When).ToList();
                    FetchedAt = DateTime.Now; Busy = false; spin.Stop();
                    ShareHeadlines();
                    refresh.Interval = c.RefreshMin * 60000; refresh.Start(); Render();
                    if (settings != null && !settings.IsDisposed) settings.FetchFinished();
                }
                finally { cancel.Dispose(); }
            })); } catch { cancel.Dispose(); }
        });
    }
    int generation;

    public void MarkAllRead() { ReadList.Add(stories.Select(s => s.Link)); Render(); }

    public static string Friendly(Exception ex)
    {
        var we = ex as WebException;
        if (we != null)
        {
            var r = we.Response as HttpWebResponse;
            if (r != null) return (int)r.StatusCode == 404 ? "Not found (404). Check the link" : "The site said " + (int)r.StatusCode + " " + r.StatusDescription;
            if (we.Status == WebExceptionStatus.NameResolutionFailure) return "Can't reach the site. Check the link or your connection";
            if (we.Status == WebExceptionStatus.Timeout) return "The site took too long to answer";
            return "Can't connect: " + we.Status;
        }
        if (ex is XmlException) return "That link isn't an RSS or Atom feed";
        return ex.Message;
    }

    // The headlines on show, for Kinetik Screen's Feeds page: %LOCALAPPDATA%\KinetikFeeds\headlines.tsv, one per line as
    // UTC ticks (0 = no date), feed name, title and link, tab-separated. Written to a temporary file first, then swapped in.
    void ShareHeadlines()
    {
        if (Program.Demo) return;
        try
        {
            var sources = c.List;
            var sb = new System.Text.StringBuilder();
            foreach (var st in ShownStories())
            {
                string name = st.Src < sources.Count ? sources[st.Src].Name : "";
                sb.Append(st.When == DateTime.MinValue ? 0 : st.When.Ticks).Append('\t').Append(Tsv(name)).Append('\t').Append(Tsv(st.Title)).Append('\t').Append(Tsv(st.Link)).Append("\r\n");
            }
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KinetikFeeds");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "headlines.tsv"); var tmp = file + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), new System.Text.UTF8Encoding(false));
            if (File.Exists(file)) File.Replace(tmp, file, null); else File.Move(tmp, file);
        }
        catch { }
    }
    static string Tsv(string v) { return (v ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' '); }

    public List<Story> ShownStories()
    {
        var sources = c.List;
        var cutoff = c.MaxAgeDays > 0 ? DateTime.UtcNow.AddDays(-c.MaxAgeDays) : DateTime.MinValue;
        return stories.Where(s => s.Src < sources.Count && sources[s.Src].On && (s.When == DateTime.MinValue || s.When >= cutoff) && !(c.HideRead && ReadList.Has(s.Link)))
            .Take(c.MaxItems).ToList();
    }

    // ---- mouse: drag to move, click a headline to open it, wheel to scroll, right-click for the menu
    int HitAt(Point p)
    {
        for (int i = 0; i < last.Hits.Count; i++) if (last.Hits[i].Contains(p)) return i;
        return -1;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            downAt = Cursor.Position; moved = false;
            if (!c.Locked) { dragging = true; grab = new Point(Cursor.Position.X - c.X, Cursor.Position.Y - c.Y); }
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (dragging && (moved || Math.Abs(Cursor.Position.X - downAt.X) + Math.Abs(Cursor.Position.Y - downAt.Y) > 4))
        {
            moved = true;
            var p = Snap(new Point(Cursor.Position.X - grab.X, Cursor.Position.Y - grab.Y));
            c.X = p.X; c.Y = p.Y;
            SetWindowPos(Handle, IntPtr.Zero, p.X, p.Y, 0, 0, 0x1 | 0x4 | 0x10); // NOSIZE | NOZORDER | NOACTIVATE
        }
        int h = dragging && moved ? -1 : HitAt(e.Location);
        if (h != hover) { hover = h; Render(); }
        bool onButton = last.RefreshHit.Contains(e.Location) || last.GearHit.Contains(e.Location);
        Cursor = dragging && moved ? Cursors.SizeAll : h >= 0 || onButton ? Cursors.Hand : Cursors.Default;
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (hover >= 0) { hover = -1; Render(); }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            bool wasDrag = dragging && moved;
            dragging = false;
            if (wasDrag) c.Save();
            else if (last.RefreshHit.Contains(e.Location)) FetchAll();
            else if (last.GearHit.Contains(e.Location)) OpenSettings(null);
            else
            {
                int i = HitAt(e.Location);
                var shown = ShownStories();
                if (i >= 0 && i < shown.Count) Open(shown[i]);
            }
        }
        else if (e.Button == MouseButtons.Right) menu.Show(Cursor.Position);
        base.OnMouseUp(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (last.MaxScroll > 0)
        {
            scroll = Math.Max(0, Math.Min(last.MaxScroll, scroll - e.Delta / 120f * 48 * last.U));
            Render();
        }
        base.OnMouseWheel(e);
    }

    void Open(Story s)
    {
        Uri uri;
        if (s.Link == null || !Uri.TryCreate(s.Link, UriKind.Absolute, out uri) || (uri.Scheme != "http" && uri.Scheme != "https")) return;
        ReadList.Add(new[] { s.Link });
        try { Process.Start(uri.ToString()); } catch { }
        Render();
    }

    Point Snap(Point p)
    {
        if (!c.Snap) return p;
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        int m = last.Margin, gap = (int)(12 * last.U), reach = (int)(18 * last.U);
        int left = p.X + m, top = p.Y + m, right = p.X + last.WinW - m, bottom = p.Y + last.WinH - m;
        if (Math.Abs(left - (wa.Left + gap)) < reach) p.X = wa.Left + gap - m;
        else if (Math.Abs(right - (wa.Right - gap)) < reach) p.X = wa.Right - gap - last.WinW + m;
        if (Math.Abs(top - (wa.Top + gap)) < reach) p.Y = wa.Top + gap - m;
        else if (Math.Abs(bottom - (wa.Bottom - gap)) < reach) p.Y = wa.Bottom - gap - last.WinH + m;
        return p;
    }

    void EnsurePosition()
    {
        if (c.X == int.MinValue || !Screen.AllScreens.Any(sc => sc.WorkingArea.IntersectsWith(new Rectangle(c.X, c.Y, last.WinW, last.WinH))))
        {
            var wa = Screen.PrimaryScreen.WorkingArea;
            int gap = (int)(12 * last.U);
            c.X = wa.Left + gap - last.Margin;
            c.Y = wa.Top + gap - last.Margin;
        }
    }

    // ---- painting
    public static string Ago(DateTime utc)
    {
        if (utc == DateTime.MinValue) return "";
        var d = DateTime.UtcNow - utc;
        if (d.TotalMinutes < 1) return "now";
        if (d.TotalMinutes < 60) return (int)d.TotalMinutes + "m";
        if (d.TotalHours < 24) return (int)d.TotalHours + "h";
        if (d.TotalDays < 7) return (int)d.TotalDays + "d";
        return utc.ToLocalTime().ToString("d MMM");
    }

    public void Render()
    {
        if (IsDisposed || !IsHandleCreated) return;
        float u = DeviceDpi / 96f * c.Scale / 100f;
        var screen = c.X == int.MinValue ? Screen.PrimaryScreen : Screen.FromPoint(new Point(c.X + 40, c.Y + 40));
        int ex = GetWindowLong(Handle, -20);
        bool through = c.Locked && c.ClickThrough;
        SetWindowLong(Handle, -20, through ? ex | 0x20 : ex & ~0x20); // TRANSPARENT: clicks fall through

        last = Compose(u, DeviceDpi / 96f, scroll, hover, screen.WorkingArea.Height - (int)(24 * u), (w, h) => surface.Canvas(w, h));
        scroll = Math.Min(scroll, last.MaxScroll);
        EnsurePosition();
        surface.Push(Handle, c.X, c.Y);
        ShowWindow(Handle, 8); // SW_SHOWNA
        if (layer == 0) SetWindowPos(Handle, new IntPtr(1), 0, 0, 0, 0, 0x1 | 0x2 | 0x10);
        else if (layer == 2) SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x1 | 0x2 | 0x10);
    }

    public class Frame
    {
        public int WinW = 1, WinH = 1, Margin;
        public float U = 1, MaxScroll;
        public List<RectangleF> Hits = new List<RectangleF>();
        public RectangleF RefreshHit, GearHit;
        public Bitmap Bmp;
    }

    static readonly StringFormat FmtStatus = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
    static readonly StringFormat FmtTitle2 = new StringFormat { Trimming = StringTrimming.EllipsisWord };
    static readonly StringFormat FmtTitle1 = new StringFormat { Trimming = StringTrimming.EllipsisWord, FormatFlags = StringFormatFlags.NoWrap };
    static readonly StringFormat FmtSource = new StringFormat { FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter };
    readonly ShadowCache shadow = new ShadowCache();

    // Lays out and draws the card into a bitmap from canvas(width, height). Used by the widget and the Settings preview.
    public Frame Compose(float u, float dpi, float scrollIn, int hoverIn, int maxScreenH, Func<int, int, Bitmap> canvas)
    {
        var f = new Frame { U = u };
        var th = Theme.Get(c.ThemeName);
        Color accent = c.Accent != 0 ? Color.FromArgb(255, Color.FromArgb(c.Accent)) : th.Accent;
        var sources = c.List;
        var shown = ShownStories();
        string family = c.FontName != "" && Draw.Has(c.FontName) ? c.FontName : Draw.Family;
        f.Margin = (int)(16 * u);
        float pad = 16 * u;
        int cardW = (int)(c.Width * u);
        float px = 1f / dpi; // fonts are sized in points at 96 dpi, then scaled by u

        using (var titleF = new Font(family, 12.5f * u * px, FontStyle.Bold, GraphicsUnit.Point))
        using (var headF = new Font(family, 10 * u * px, GraphicsUnit.Point))
        using (var subF = new Font(family, 8.25f * u * px, GraphicsUnit.Point))
        using (var badgeF = new Font(family, 7.75f * u * px, FontStyle.Bold, GraphicsUnit.Point))
        using (var measure = new Bitmap(1, 1))
        using (var mg = Graphics.FromImage(measure))
        {
            mg.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            float textW = cardW - pad * 2 - 16 * u;
            float lineH = headF.GetHeight(mg), subH = subF.GetHeight(mg);
            var heights = shown.Select(s =>
            {
                float h = Math.Min(mg.MeasureString(s.Title, headF, (int)textW).Height, lineH * (c.TwoLines ? 2 : 1));
                return h + (c.ShowSource ? subH + 3 * u : 0) + 14 * u;
            }).ToList();
            float headerH = c.ShowHeader ? titleF.GetHeight(mg) + 16 * u : 0;
            float listH = heights.Sum();
            if (shown.Count == 0) listH = lineH * 2 + 8 * u;
            float maxList = Math.Min(c.MaxHeight * u, maxScreenH) - headerH - pad * 2;
            float visibleH = Math.Max(lineH * 2, Math.Min(listH, maxList));
            f.MaxScroll = Math.Max(0, listH - visibleH);
            scrollIn = Math.Min(scrollIn, f.MaxScroll);
            int cardH = (int)(pad * 2 + headerH + visibleH - (c.ShowHeader ? 0 : 4 * u));
            f.WinW = cardW + f.Margin * 2; f.WinH = cardH + f.Margin * 2;

            var bmp = f.Bmp = canvas(f.WinW, f.WinH);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                var card = new RectangleF(f.Margin, f.Margin, cardW, cardH);
                float rad = 14 * u * c.Round / 100f;
                if (c.Shadow)
                    shadow.Paint(g, f.WinW, f.WinH, card + "|" + rad + "|" + u + "|" + c.Opacity, sg =>
                    {
                        for (int i = 1; i <= 7; i++)
                            using (var p = Draw.Round(new RectangleF(card.X - i * 1.5f * u, card.Y - i * 1.2f * u + 2 * u, card.Width + i * 3f * u, card.Height + i * 3f * u), rad + i * 1.5f * u))
                            using (var b = new SolidBrush(Color.FromArgb((int)(16 * Math.Max(0.35f, c.Opacity / 100f) / i), 0, 0, 0))) sg.FillPath(b, p);
                    });
                Draw.Fill(g, card, rad, Color.FromArgb(255 * c.Opacity / 100, th.Bg));
                if (c.Border) Draw.Stroke(g, card, rad, th.Light ? Color.FromArgb(34, 0, 0, 0) : Color.FromArgb(30, 255, 255, 255), 1);

                using (var text = new SolidBrush(th.Text))
                using (var sub = new SolidBrush(th.Sub))
                using (var dim = new SolidBrush(Draw.Mix(th.Sub, th.Bg, 0.3f)))
                {
                    float x = card.X + pad, y = card.Y + pad, right = card.Right - pad;
                    if (c.ShowHeader)
                    {
                        float hh = titleF.GetHeight(g);
                        float gs = 15 * u;
                        UI.Glyph(g, "rss", new RectangleF(x, y + (hh - gs) / 2, gs, gs), accent);
                        float tx = x + gs + 8 * u;
                        string title = c.Title.Trim() == "" ? "Feeds" : c.Title.Trim();
                        g.DrawString(title, titleF, text, tx, y);
                        tx += g.MeasureString(title, titleF).Width;

                        int unread = shown.Count(s => !ReadList.Has(s.Link));
                        if (c.UnreadBadge && unread > 0)
                        {
                            string n = unread + " new";
                            var sz = g.MeasureString(n, badgeF);
                            var pill = new RectangleF(tx + 2 * u, y + (hh - sz.Height - 2 * u) / 2, sz.Width + 8 * u, sz.Height + 2 * u);
                            Draw.Fill(g, pill, pill.Height / 2, Color.FromArgb(th.Light ? 34 : 46, accent));
                            using (var ab = new SolidBrush(th.Light ? Draw.Mix(accent, Color.Black, 0.25f) : Draw.Mix(accent, Color.White, 0.25f)))
                                g.DrawString(n, badgeF, ab, pill.X + 4 * u, pill.Y + 1 * u);
                        }

                        // buttons on the right: settings, refresh
                        float bs = 22 * u, by = y + (hh - bs) / 2;
                        f.GearHit = new RectangleF(right - bs + 3 * u, by, bs, bs);
                        f.RefreshHit = new RectangleF(right - bs * 2, by, bs, bs);
                        UI.Glyph(g, "gear", RectangleF.Inflate(f.GearHit, -4 * u, -4 * u), th.Sub);
                        if (Busy)
                        {
                            float sweep = (float)(DateTime.Now.TimeOfDay.TotalMilliseconds / 3 % 360);
                            using (var pen = new Pen(accent, 1.8f * u) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                                g.DrawArc(pen, RectangleF.Inflate(f.RefreshHit, -5 * u, -5 * u), sweep, 270);
                        }
                        else UI.Glyph(g, "sync", RectangleF.Inflate(f.RefreshHit, -4 * u, -4 * u), th.Sub);

                        string status = Busy && FetchedAt == DateTime.MinValue ? "Loading…" : Errors.Count > 0 && Errors.Count >= sources.Count(s => s.On) ? "Offline"
                            : Errors.Count > 0 ? "⚠ " + Errors.Count + " failed" : "";
                        if (status != "") g.DrawString(status, subF, sub, new RectangleF(tx, y, f.RefreshHit.X - 4 * u - tx, hh), FmtStatus);
                        y += headerH;
                        using (var pen = new Pen(th.Line, 1)) g.DrawLine(pen, x, y - 8 * u, right, y - 8 * u);
                    }
                    else y -= 4 * u;

                    var listArea = new RectangleF(card.X, y - 4 * u, card.Width, visibleH + 4 * u);
                    g.SetClip(listArea);
                    if (shown.Count == 0)
                    {
                        string msg = Busy ? "Loading headlines…" : sources.Count == 0 ? "No feeds yet. Click the gear to add one." : sources.All(s => !s.On) ? "All feeds are switched off." : Errors.Count > 0 ? "Couldn't load your feeds. Check your connection." : "Nothing new.";
                        g.DrawString(msg, headF, sub, new RectangleF(x, y + 4 * u, right - x, lineH * 2));
                    }
                    float iy = y - scrollIn;
                    var fmtT = c.TwoLines ? FmtTitle2 : FmtTitle1;
                    var fmtS = FmtSource;
                    for (int i = 0; i < shown.Count; i++)
                    {
                        var s = shown[i];
                        var row = new RectangleF(card.X + pad * 0.45f, iy, card.Width - pad * 0.9f, heights[i] - 2 * u);
                        f.Hits.Add(RectangleF.Intersect(row, listArea));
                        if (i == hoverIn)
                            Draw.Fill(g, row, 9 * u, th.Light ? Color.FromArgb(14, 0, 0, 0) : Color.FromArgb(20, 255, 255, 255));

                        bool isRead = ReadList.Has(s.Link);
                        var col = s.Src < sources.Count ? sources[s.Src].Color : th.Sub;
                        float ty = iy + 7 * u, tx = x + 16 * u;
                        float titleH = Math.Min(g.MeasureString(s.Title, headF, (int)textW).Height, lineH * (c.TwoLines ? 2 : 1));
                        // a colour bar down the left for the source; hollow once read
                        var bar = new RectangleF(x + 1 * u, ty + 2 * u, 3 * u, titleH - 4 * u + (c.ShowSource ? subH + 3 * u : 0));
                        Draw.Fill(g, bar, 1.5f * u, isRead ? Color.FromArgb(70, col) : col);
                        g.DrawString(s.Title, headF, isRead ? dim : text, new RectangleF(tx, ty, textW, titleH + 1), fmtT);
                        if (c.ShowSource)
                        {
                            string src = s.Src < sources.Count ? sources[s.Src].Name : "";
                            string ago = Ago(s.When);
                            g.DrawString(src + (ago != "" && src != "" ? "  ·  " : "") + ago, subF, isRead ? dim : sub, new RectangleF(tx, ty + titleH + 3 * u, textW, subH + 2), fmtS);
                        }
                        iy += heights[i];
                    }
                    g.ResetClip();

                    // soft fades where the list continues above or below
                    if (f.MaxScroll > 0)
                    {
                        float fh = 18 * u;
                        Color bg = Color.FromArgb(255 * c.Opacity / 100, th.Bg), clear = Color.FromArgb(0, th.Bg);
                        if (scrollIn > 1)
                            using (var lg = new LinearGradientBrush(new RectangleF(card.X, y - 4 * u, card.Width, fh + 1), bg, clear, 90f))
                                g.FillRectangle(lg, card.X + 1, y - 4 * u, card.Width - 2, fh);
                        if (scrollIn < f.MaxScroll - 1)
                            using (var lg = new LinearGradientBrush(new RectangleF(card.X, y + visibleH - fh, card.Width, fh + 1), clear, bg, 90f))
                                g.FillRectangle(lg, card.X + 1, y + visibleH - fh, card.Width - 2, fh);
                        float barH = Math.Max(24 * u, visibleH * visibleH / listH);
                        float by = y + (visibleH - barH) * scrollIn / f.MaxScroll;
                        Draw.Fill(g, new RectangleF(card.Right - 6 * u, by, 3 * u, barH), 1.5f * u, th.Light ? Color.FromArgb(50, 0, 0, 0) : Color.FromArgb(50, 255, 255, 255));
                    }
                }
            }
        }
        return f;
    }

    // ---- quick menu (everything else is in Settings)
    ToolStripMenuItem Item(string text, bool check, Action click)
    {
        var it = new ToolStripMenuItem(text) { Checked = check };
        it.Click += (s, e) => click();
        return it;
    }

    void BuildMenu()
    {
        var old = menu.Items.Cast<ToolStripItem>().ToList();
        menu.Items.Clear();
        foreach (var it in old) it.Dispose(); // a cleared submenu that was shown keeps its window otherwise
        menu.Items.Add(Item("Add a feed…", false, () => OpenSettings("Feeds")));
        menu.Items.Add(Item(Busy ? "Updating…" : "Refresh now", false, FetchAll));
        menu.Items.Add(Item("Mark all as read", false, MarkAllRead));
        menu.Items.Add(new ToolStripSeparator());
        Common().AddAll(menu.Items);
    }

    // The look, placement, startup and backup options, shared word for word with the other Kinetik apps.
    CommonMenu Common()
    {
        return new CommonMenu
        {
            Title = "Kinetik Feeds", Version = Program.Version,
            OpenTheme = () => OpenSettings("Look"),
            Scale = () => c.Scale, SetScale = v => { c.Scale = v; Apply(); },
            Widths = new[] { 300, 340, 400 }, CardWidth = () => c.Width, SetCardWidth = v => { c.Width = v; Apply(); },
            FontName = () => c.FontName, SetFontName = v => { c.FontName = v; Apply(); },
            Opacity = () => c.Opacity, SetOpacity = v => { c.Opacity = v; Apply(); },
            Layer = () => c.Layer, SetLayer = v => { c.Layer = v; c.Save(); RecreateWidget(); },
            Locked = () => c.Locked, SetLocked = v => { c.Locked = v; Apply(); },
            ClickThrough = () => c.ClickThrough, SetClickThrough = v => { c.ClickThrough = v; Apply(); },
            Snap = () => c.Snap, SetSnap = v => { c.Snap = v; Apply(); },
            OpenSettings = () => OpenSettings(null),
            RunAtStartup = () => Startup.IsOn, SetRunAtStartup = Startup.Set,
            Restore = Backup.RestoreNow, LastBackup = () => Backup.SavedText,
            BackUp = () => { c.Save(); Backup.SaveNow(); },
            Exit = () => { tray.Visible = false; Application.Exit(); },
        };
    }
}

// ======================================================================= Settings window
// The widget as it will look, redrawn after every change.
class PreviewBox : Control
{
    readonly FeedsForm app;
    Bitmap cache;
    bool dirty = true;

    public PreviewBox(FeedsForm app)
    {
        this.app = app;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = UI.Page;
    }

    public void Changed() { dirty = true; Invalidate(); }

    protected override void Dispose(bool disposing)
    {
        if (disposing && cache != null) cache.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        TextRenderer.DrawText(g, "PREVIEW", UI.F(11, FontStyle.Bold), new Point(UI.I(18), UI.I(20)), UI.Sub);
        var area = new RectangleF(UI.I(16), UI.I(46), Width - UI.I(32), Height - UI.I(62));
        using (var path = Draw.Round(area, UI.I(12)))
        using (var lg = new LinearGradientBrush(area, Color.FromArgb(38, 52, 92), Color.FromArgb(78, 42, 86), 60f))
        {
            g.FillPath(lg, path);
            var st = g.Save();
            g.SetClip(path);
            if (dirty || cache == null)
            {
                dirty = false;
                if (cache != null) cache.Dispose();
                cache = null;
                float u = UI.S * app.Cfg.Scale / 100f;
                app.Compose(u, UI.S, 0, -1, (int)(area.Height * 1.6f), (w, h) => cache = new Bitmap(w, h, PixelFormat.Format32bppPArgb));
            }
            float fit = Math.Min(1, (area.Width - UI.I(8)) / cache.Width);
            float w2 = cache.Width * fit, h2 = cache.Height * fit;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(cache, area.X + (area.Width - w2) / 2, area.Y + UI.I(4), w2, h2);
            g.Restore(st);
        }
    }
}

class SettingsForm : Form
{
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr h, string app, string id);

    readonly FeedsForm app;
    readonly Settings c;
    readonly Panel side = new Panel(), host = new Panel();
    readonly PreviewBox preview;
    readonly List<NavItem> navs = new List<NavItem>();
    readonly System.Windows.Forms.Timer previewTimer = new System.Windows.Forms.Timer { Interval = 40 };
    FlowLayoutPanel page;
    string current;
    int contentW;

    static readonly string[][] Pages =
    {
        new[] { "Feeds", "rss" }, new[] { "Headlines", "news" }, new[] { "Look", "palette" },
        new[] { "Behaviour", "gear" }, new[] { "About", "info" },
    };

    public SettingsForm(FeedsForm app, string startPage)
    {
        this.app = app; c = app.Cfg;
        UI.S = DeviceDpi / 96f;
        Text = "Kinetik Feeds";
        Icon = app.AppIcon;
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = UI.Page; ForeColor = UI.Text;
        Font = UI.F(13);
        ClientSize = new Size(UI.I(1030), UI.I(720));
        DoubleBuffered = true;

        side.Dock = DockStyle.Left; side.Width = UI.I(184); side.BackColor = UI.Side;
        side.Paint += (s, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            UI.Glyph(g, "rss", new RectangleF(UI.I(20), UI.I(24), UI.I(22), UI.I(22)), UI.Accent);
            TextRenderer.DrawText(g, "Kinetik", UI.F(17, FontStyle.Bold), new Point(UI.I(50), UI.I(18)), UI.Text);
            TextRenderer.DrawText(g, "Feeds", UI.F(12), new Point(UI.I(51), UI.I(40)), UI.Sub);
            TextRenderer.DrawText(g, "v" + Program.Version, UI.F(11.5f), new Point(UI.I(22), side.Height - UI.I(30)), UI.Sub);
        };
        int y = UI.I(84);
        foreach (var pg in Pages)
        {
            var n = new NavItem(pg[0], pg[1]) { Location = new Point(UI.I(10), y) };
            n.Click += (s, e) => ShowPage(((NavItem)s).Page);
            navs.Add(n); side.Controls.Add(n);
            y += UI.I(44);
        }

        preview = new PreviewBox(app) { Dock = DockStyle.Right, Width = UI.I(330) };
        host.Dock = DockStyle.Fill; host.BackColor = UI.Page;
        Controls.Add(host); Controls.Add(preview); Controls.Add(side);
        previewTimer.Tick += (s, e) => { previewTimer.Stop(); preview.Changed(); };
        ShowPage(startPage);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int one = 1; DwmSetWindowAttribute(Handle, 20, ref one, 4); // dark title bar
        int round = 2; DwmSetWindowAttribute(Handle, 33, ref round, 4);
        int caption = ColorTranslator.ToWin32(UI.Side); DwmSetWindowAttribute(Handle, 35, ref caption, 4);
    }

    protected override void Dispose(bool disposing) { if (disposing) previewTimer.Dispose(); base.Dispose(disposing); }

    public void PreviewChanged() { previewTimer.Stop(); previewTimer.Start(); }

    public void FetchFinished() { if (current == "Feeds") ShowPage("Feeds"); else PreviewChanged(); }

    // ---- pages
    public void ShowPage(string name)
    {
        current = name;
        foreach (var n in navs) { n.Selected = n.Page == name; n.Invalidate(); }
        int scroll = page != null && name == (string)page.Tag ? -page.AutoScrollPosition.Y : 0;
        host.SuspendLayout();
        var old = page;
        page = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = UI.Page,
            Padding = new Padding(UI.I(24), UI.I(18), UI.I(12), UI.I(24)), Tag = name,
        };
        contentW = host.ClientSize.Width - UI.I(24) - UI.I(12) - SystemInformation.VerticalScrollBarWidth - UI.I(2);
        Title(name);
        switch (name)
        {
            case "Feeds": BuildFeeds(); break;
            case "Headlines": BuildHeadlines(); break;
            case "Look": BuildLook(); break;
            case "Behaviour": BuildBehaviour(); break;
            default: BuildAbout(); break;
        }
        host.Controls.Add(page);
        if (old != null) { host.Controls.Remove(old); old.Dispose(); }
        host.ResumeLayout();
        SetWindowTheme(page.Handle, "DarkMode_Explorer", null);
        if (scroll > 0) page.AutoScrollPosition = new Point(0, scroll);
        PreviewChanged();
    }

    void Title(string t)
    {
        page.Controls.Add(new Label { Text = t, Font = UI.F(22, FontStyle.Bold), ForeColor = UI.Text, AutoSize = true, Margin = new Padding(0, 0, 0, UI.I(4)) });
    }

    void Desc(string t)
    {
        page.Controls.Add(new Label { Text = t, Font = UI.F(12.5f), ForeColor = UI.Sub, MaximumSize = new Size(contentW, 0), AutoSize = true, Margin = new Padding(0, 0, 0, UI.I(14)) });
    }

    void Section(string t)
    {
        page.Controls.Add(new Label { Text = t, Font = UI.F(13.5f, FontStyle.Bold), ForeColor = Color.FromArgb(180, 190, 210), AutoSize = true, Margin = new Padding(0, UI.I(10), 0, UI.I(10)) });
    }

    Card Row(string glyph, string title, string sub, Control right)
    {
        var card = new Card(glyph, title, sub, right, contentW);
        page.Controls.Add(card);
        return card;
    }

    void Changed() { app.Apply(); }

    void Switch(string glyph, string title, string sub, bool value, Action<bool> set)
    {
        var t = new Toggle(value);
        t.Changed += (s, e) => { set(t.Checked); Changed(); };
        Row(glyph, title, sub, t);
    }

    void Slide(string glyph, string title, string sub, int min, int max, int value, Func<int, string> fmt, Action<int> set)
    {
        var sl = new Slider(min, max, value);
        if (fmt != null) sl.Format = fmt;
        sl.Changed += (s, e) => { set(sl.Value); Changed(); };
        Row(glyph, title, sub, sl);
    }

    DarkCombo Choice(string glyph, string title, string sub, string[] items, int sel, Action<int> set)
    {
        var cb = new DarkCombo(items, sel);
        cb.SelectedIndexChanged += (s, e) => { set(cb.SelectedIndex); Changed(); };
        Row(glyph, title, sub, cb);
        return cb;
    }

    FlowLayoutPanel Buttons(params Button[] buttons)
    {
        var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = UI.Page, Margin = new Padding(0, UI.I(2), 0, UI.I(10)) };
        foreach (var b in buttons) { b.Margin = new Padding(0, 0, UI.I(8), 0); f.Controls.Add(b); }
        page.Controls.Add(f);
        return f;
    }

    // ---- Feeds
    void BuildFeeds()
    {
        Desc("News sites, blogs and anything else with an RSS or Atom feed. Paste a feed link or just the website's address. Switch a feed off to hide its headlines, or click it to change its name, colour or link.");
        var list = c.List;
        if (list.Count == 0)
        {
            var empty = Row("plus", "No feeds yet", "Add your first one, or pick from the suggestions.", null);
            empty.Cursor = Cursors.Hand;
            empty.Click += (s, e) => AddFeed();
        }
        for (int i = 0; i < list.Count; i++)
        {
            var f = list[i];
            int idx = i;
            var t = new Toggle(f.On);
            t.Changed += (s, e) => { var l = c.List; l[idx].On = t.Checked; c.List = l; app.FeedsChanged(); };
            string err, status;
            int n;
            bool failed = app.Errors.TryGetValue(i, out err);
            if (!f.On) status = "Off";
            else if (failed) status = "⚠ " + err;
            else if (app.Counts.TryGetValue(i, out n)) status = n + " headline" + (n == 1 ? "" : "s");
            else status = app.Busy ? "Checking…" : "Not checked yet";
            Uri u;
            string host = Uri.TryCreate(f.Url, UriKind.Absolute, out u) ? u.Host.Replace("www.", "") : f.Url;
            var card = Row(null, f.Name, host + "  ·  " + status, t);
            card.Swatch = f.Color;
            if (failed && f.On) card.SubColor = UI.Warn;
            card.Cursor = Cursors.Hand;
            card.Click += (s, e) => EditFeed(idx);
        }
        Buttons(UI.Btn("+  Add feed", (s, e) => AddFeed(), true), UI.Btn(app.Busy ? "Updating…" : "Refresh now", (s, e) => { app.FetchAll(); ShowPage("Feeds"); }));

        Section("Updating");
        var mins = new[] { 10, 15, 30, 60, 120, 240 };
        Choice("sync", "Check every", "Also checked when the PC wakes up", new[] { "10 minutes", "15 minutes", "30 minutes", "1 hour", "2 hours", "4 hours" },
            Math.Max(0, Array.IndexOf(mins, c.RefreshMin)), i => c.RefreshMin = mins[i]);
    }

    void AddFeed()
    {
        using (var d = new FeedDialog(null, c.List.Count))
            if (d.ShowDialog(this) == DialogResult.OK)
            {
                var l = c.List; l.Add(d.Result); c.List = l;
                app.FeedsChanged(); ShowPage("Feeds");
            }
    }

    void EditFeed(int i)
    {
        var l = c.List;
        if (i < 0 || i >= l.Count) return;
        using (var d = new FeedDialog(l[i], i))
        {
            var res = d.ShowDialog(this);
            if (res == DialogResult.Abort) l.RemoveAt(i);
            else if (res == DialogResult.OK) { d.Result.On = l[i].On; l[i] = d.Result; }
            else return;
            c.List = l; app.FeedsChanged(); ShowPage("Feeds");
        }
    }

    // ---- Headlines
    void BuildHeadlines()
    {
        Desc("Headlines from every feed are merged into one list, newest first. Click one to open it in your browser; it then dims so you can see what you've read.");
        Section("List");
        Slide("list", "Headlines to show", null, 3, 60, c.MaxItems, null, v => c.MaxItems = v);
        var ages = new[] { 0, 1, 2, 3, 7, 14 };
        Choice("clock", "Hide headlines older than", null, new[] { "Show everything", "1 day", "2 days", "3 days", "1 week", "2 weeks" }, Math.Max(0, Array.IndexOf(ages, c.MaxAgeDays)), i => c.MaxAgeDays = ages[i]);
        Switch("eye", "Hide headlines I've read", "Otherwise they stay, dimmed", c.HideRead, v => c.HideRead = v);
        Section("Each headline");
        Switch("text", "Two-line titles", "Long titles wrap onto a second line", c.TwoLines, v => c.TwoLines = v);
        Switch("marker", "Source and time", "\"BBC News · 25m\" under the title", c.ShowSource, v => c.ShowSource = v);
        Section("Header");
        Switch("layout", "Show header", "Title, unread count, refresh and settings buttons", c.ShowHeader, v => c.ShowHeader = v);
        var tb = new TextBox { Text = c.Title };
        tb.TextChanged += (s, e) => { c.Title = tb.Text; Changed(); };
        Row("text", "Title", "Shown at the top of the card", UI.TextField(tb, UI.I(210)));
        Switch("bolt", "Unread count", "A small \"5 new\" badge next to the title", c.UnreadBadge, v => c.UnreadBadge = v);
        Buttons(UI.Btn("Mark all as read", (s, e) => { app.MarkAllRead(); Changed(); }), UI.Btn("Mark all as unread", (s, e) => { ReadList.Clear(); Changed(); }));
    }

    // ---- Look
    void BuildLook()
    {
        Desc("Themes set the background, text and accent colours. Each feed keeps its own colour, set on the Feeds page.");
        Section("Theme");
        var gallery = new FlowLayoutPanel { Width = contentW + UI.I(8), AutoSize = true, MaximumSize = new Size(contentW + UI.I(8), 0), BackColor = UI.Page, Margin = new Padding(0, 0, 0, UI.I(6)) };
        var tiles = new List<ThemeTile>();
        foreach (var name in new[] { "Auto" }.Concat(Theme.All.Select(t => t.Name)))
        {
            var tile = new ThemeTile(name, c.ThemeName == name);
            tile.Click += (s, e) =>
            {
                c.ThemeName = ((ThemeTile)s).ThemeName;
                foreach (var t in tiles) t.SetSelected(t == s);
                Changed();
            };
            tiles.Add(tile); gallery.Controls.Add(tile);
        }
        page.Controls.Add(gallery);

        Section("Colour");
        ColourRow("drop", "Accent colour", "The icon, unread badge and refresh spinner", () => c.Accent, v => c.Accent = v);

        Section("Card");
        var fonts = Draw.FontChoices.Where(Draw.Has).ToList();
        fonts.Insert(0, "Default");
        Choice("text", "Font", null, fonts.ToArray(), Math.Max(0, fonts.IndexOf(c.FontName)), i => c.FontName = i == 0 ? "" : fonts[i]);
        Slide("size", "Size", null, 70, 200, c.Scale, v => v + "%", v => c.Scale = v);
        Slide("width", "Width", null, 220, 700, c.Width, v => v + " px", v => c.Width = v);
        Slide("height", "Maximum height", "Longer lists scroll with the mouse wheel", 200, 1400, c.MaxHeight, v => v + " px", v => c.MaxHeight = v);
        Slide("opacity", "Background opacity", null, 0, 100, c.Opacity, v => v + "%", v => c.Opacity = v);
        Slide("round", "Corner roundness", null, 0, 200, c.Round, v => v + "%", v => c.Round = v);
        Switch("shadow", "Shadow", null, c.Shadow, v => c.Shadow = v);
        Switch("border", "Outline", "A hairline around the card", c.Border, v => c.Border = v);
        Buttons(UI.Btn("Restore default look", (s, e) => { c.ResetLook(); Changed(); ShowPage("Look"); }));
    }

    void ColourRow(string glyph, string title, string sub, Func<int> get, Action<int> set)
    {
        var flow = new Panel { Size = new Size(UI.I(250), UI.I(30)) };
        int fx = UI.I(70);
        var opts = new List<Swatch>();
        Action refresh = null;
        var themeBtn = UI.Btn("Theme", (s, e) => { set(0); Changed(); refresh(); });
        themeBtn.AutoSize = false; themeBtn.MinimumSize = Size.Empty; themeBtn.Padding = Padding.Empty;
        themeBtn.SetBounds(0, UI.I(1), UI.I(64), UI.I(28));
        flow.Controls.Add(themeBtn);
        foreach (var col in new[] { Color.FromArgb(240, 88, 88), Color.FromArgb(77, 166, 255), Color.FromArgb(108, 201, 124), Color.FromArgb(255, 159, 10), Color.FromArgb(255, 121, 198) })
        {
            var sw = new Swatch(col, UI.I(28)) { Location = new Point(fx, UI.I(1)) };
            fx += UI.I(30);
            sw.Click += (s, e) => { set(((Swatch)s).Color.ToArgb()); Changed(); refresh(); };
            opts.Add(sw); flow.Controls.Add(sw);
        }
        var custom = UI.Btn("…", (s, e) =>
        {
            using (var d = new ColorDialog { FullOpen = true, Color = get() != 0 ? Color.FromArgb(get()) : Color.White })
                if (d.ShowDialog(this) == DialogResult.OK) { set(Color.FromArgb(255, d.Color).ToArgb()); Changed(); refresh(); }
        });
        custom.AutoSize = false; custom.MinimumSize = Size.Empty; custom.Padding = Padding.Empty;
        custom.SetBounds(fx + UI.I(2), UI.I(1), UI.I(32), UI.I(28));
        flow.Controls.Add(custom);
        refresh = () =>
        {
            foreach (var sw in opts) { sw.Selected = get() != 0 && sw.Color.ToArgb() == get(); sw.Invalidate(); }
            themeBtn.BackColor = get() == 0 ? Color.FromArgb(46, 96, 168) : UI.Field;
        };
        refresh();
        Row(glyph, title, sub, flow);
    }

    // ---- Behaviour
    void BuildBehaviour()
    {
        Desc("Where the widget sits and how it responds. Drag it to move it, scroll it with the mouse wheel, and right-click it for quick options.");
        Section("Window");
        Choice("layers", "Placement", "Behind, among or above windows", new[] { "On the desktop (behind windows)", "Normal window", "Always on top" }, c.Layer,
            i => { c.Layer = i; c.Save(); app.RecreateWidget(); });
        Switch("lock", "Lock position", "Stops it being dragged by accident", c.Locked, v => c.Locked = v);
        Switch("pointer", "Click-through when locked", "Clicks pass through. Unlock from the tray icon", c.ClickThrough, v => c.ClickThrough = v);
        Switch("magnet", "Snap to screen edges", null, c.Snap, v => c.Snap = v);
        Row("width", "Position", "Moves it back to the top-left corner", UI.Btn("Reset position", (s, e) => { c.X = c.Y = int.MinValue; Changed(); }));
        Section("Startup");
        var t = new Toggle(Startup.IsOn);
        t.Changed += (s, e) => Startup.Set(t.Checked);
        Row("power", "Run at startup", "Starts with Windows", t);
        Section("Backup");
        Row("folder", "Back up settings", "Saves your feeds, look and layout next to the app. Last backup: " + Backup.SavedText,
            UI.Btn("Back up", (s, e) => { c.Save(); Backup.SaveNow(); ShowPage(current); }));
        Row("folder", "Restore settings", "Loads the backup from the app folder, for example after reinstalling Windows",
            UI.Btn("Restore", (s, e) => Backup.RestoreNow()));
    }

    // ---- About
    void BuildAbout()
    {
        Desc("An RSS and Atom headlines widget, in the style of Kinetik.");
        Row("info", "Kinetik Feeds " + Program.Version, "Copyright © 2026 Akila Sella Hennedige. GPL-3.0-or-later.", null);
        long mem = 0;
        try { using (var pr = Process.GetCurrentProcess()) mem = pr.WorkingSet64; } catch { }
        Row("memory", "Memory in use: " + (mem / 1048576.0).ToString("0.0") + " MB", "Feeds are only fetched every " + c.RefreshMin + " minutes; in between it just updates the times once a minute.", null);
        Row("folder", "Saved data", "Which headlines you've read is kept in " + ReadList.Dir + ". Feeds and settings are in HKCU\\Software\\KinetikFeeds.",
            UI.Btn("Open folder", (s, e) => { try { Directory.CreateDirectory(ReadList.Dir); Process.Start(new ProcessStartInfo(ReadList.Dir) { UseShellExecute = true }); } catch { } }));
    }
}

// Add or edit a feed: paste a link (or a site's address), check it, pick a name and colour.
class FeedDialog : Form
{
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);

    static readonly string[][] Suggestions =
    {
        new[] { "ABC News", "https://www.abc.net.au/news/feed/51120/rss.xml" },
        new[] { "BBC World", "https://feeds.bbci.co.uk/news/world/rss.xml" },
        new[] { "The Guardian", "https://www.theguardian.com/world/rss" },
        new[] { "The Verge", "https://www.theverge.com/rss/index.xml" },
        new[] { "Ars Technica", "https://feeds.arstechnica.com/arstechnica/index" },
        new[] { "Hacker News", "https://hnrss.org/frontpage" },
    };

    readonly TextBox name = new TextBox(), url = new TextBox();
    readonly Label result = new Label();
    readonly Button check, ok;
    readonly List<Swatch> swatches = new List<Swatch>();
    Color color;
    string checkedUrl, foundUrl;
    readonly string originalUrl;
    bool nameTyped;
    public Source Result;

    public FeedDialog(Source f, int index)
    {
        Text = f == null ? "Add a feed" : "Edit feed";
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = UI.Page; ForeColor = UI.Text;
        Font = UI.F(13);
        int L = UI.I(24), W = UI.I(520), y = UI.I(20);

        Controls.Add(new Label { Text = f == null ? "Add a feed" : "Edit feed", Font = UI.F(20, FontStyle.Bold), AutoSize = true, Location = new Point(L - UI.I(2), y) });
        y += UI.I(40);
        Controls.Add(new Label { Text = "Paste a feed link, or just a website's address and the feed will be found for you.", ForeColor = UI.Sub, Font = UI.F(12.5f), Location = new Point(L, y), Size = new Size(W, UI.I(22)) });
        y += UI.I(34);

        Func<string, Label> label = t => { var l = new Label { Text = t, AutoSize = true, Location = new Point(L, y), ForeColor = UI.Sub, Font = UI.F(12, FontStyle.Bold) }; Controls.Add(l); return l; };

        label("LINK");
        y += UI.I(22);
        var linkBox = Field(url, L, y, W - UI.I(104));
        check = UI.Btn("Check", (s, e) => Check(false));
        check.AutoSize = false; check.SetBounds(L + W - UI.I(94), y, UI.I(94), UI.I(36));
        Controls.Add(check);
        y += UI.I(44);
        result.SetBounds(L, y, W, UI.I(22)); result.ForeColor = UI.Sub; result.Font = UI.F(12.5f);
        Controls.Add(result);
        y += UI.I(30);

        if (f == null)
        {
            label("SUGGESTIONS");
            y += UI.I(24);
            int sx = L;
            foreach (var sg in Suggestions)
            {
                var pair = sg;
                var chip = UI.Btn(sg[0], (s, e) => { url.Text = pair[1]; if (!nameTyped) name.Text = pair[0]; nameTyped = false; Check(false); });
                chip.Font = UI.F(12); chip.MinimumSize = new Size(0, UI.I(30)); chip.Padding = new Padding(UI.I(4), 0, UI.I(4), 0);
                chip.Location = new Point(sx, y);
                Controls.Add(chip);
                sx += chip.PreferredSize.Width + UI.I(6);
                if (sx > L + W - UI.I(100)) { sx = L; y += UI.I(36); }
            }
            if (sx != L) y += UI.I(36);
            y += UI.I(12);
        }

        label("NAME");
        y += UI.I(22);
        Field(name, L, y, UI.I(300));
        name.TextChanged += (s, e) => { if (name.Focused) nameTyped = true; };
        y += UI.I(52);

        label("COLOUR");
        y += UI.I(24);
        int cx = L;
        foreach (var col in Rss.Palette)
        {
            var sw = new Swatch(col, UI.I(28)) { Location = new Point(cx, y), BackColor = UI.Page };
            sw.Click += (s, e) => { color = ((Swatch)s).Color; MarkSwatch(); };
            swatches.Add(sw); Controls.Add(sw);
            cx += UI.I(32);
        }
        var more = UI.Btn("…", (s, e) =>
        {
            using (var d = new ColorDialog { Color = color, FullOpen = true })
                if (d.ShowDialog(this) == DialogResult.OK) { color = Color.FromArgb(255, d.Color); MarkSwatch(); }
        });
        more.AutoSize = false; more.MinimumSize = Size.Empty; more.SetBounds(cx + UI.I(4), y, UI.I(34), UI.I(28));
        Controls.Add(more);
        y += UI.I(54);

        ok = UI.Btn(f == null ? "Add feed" : "Save", (s, e) => Accept(), true);
        var cancel = UI.Btn("Cancel", (s, e) => { DialogResult = DialogResult.Cancel; Close(); });
        ok.AutoSize = cancel.AutoSize = false;
        ok.SetBounds(L + W - UI.I(212), y, UI.I(110), UI.I(36)); cancel.SetBounds(L + W - UI.I(94), y, UI.I(94), UI.I(36));
        Controls.Add(ok); Controls.Add(cancel);
        if (f != null)
        {
            var remove = UI.Btn("Remove", (s, e) =>
            {
                if (MessageBox.Show(this, "Remove \"" + f.Name + "\" from the widget?", "Remove feed", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
                { DialogResult = DialogResult.Abort; Close(); }
            });
            remove.ForeColor = Color.FromArgb(255, 130, 120);
            remove.AutoSize = false; remove.SetBounds(L, y, UI.I(94), UI.I(36));
            Controls.Add(remove);
        }
        AcceptButton = ok; CancelButton = cancel;
        ClientSize = new Size(L * 2 + W, y + UI.I(36) + UI.I(24));

        color = f != null ? f.Color : Rss.Palette[index % Rss.Palette.Length];
        name.Text = f != null ? f.Name : "";
        nameTyped = f != null;
        url.Text = originalUrl = f != null ? f.Url : "";
        url.TextChanged += (s, e) => { if (url.Text.Trim() != checkedUrl) { result.Text = ""; } };
        MarkSwatch();
    }

    // A text box inside a rounded, padded field, like the rest of the window.
    Panel Field(TextBox t, int x, int y, int w)
    {
        var p = new Panel { Location = new Point(x, y), Size = new Size(w, UI.I(36)), BackColor = UI.Page };
        p.Paint += (s, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Draw.Fill(e.Graphics, new RectangleF(0, 0, p.Width - 1, p.Height - 1), UI.I(7), UI.Field);
            Draw.Stroke(e.Graphics, new RectangleF(0.5f, 0.5f, p.Width - 2, p.Height - 2), UI.I(7), t.Focused ? UI.Accent : UI.Edge, 1);
        };
        t.BorderStyle = BorderStyle.None; t.BackColor = UI.Field; t.ForeColor = UI.Text; t.Font = UI.F(13.5f);
        t.SetBounds(UI.I(10), (p.Height - t.PreferredHeight) / 2, w - UI.I(20), t.PreferredHeight);
        t.GotFocus += (s, e) => p.Invalidate();
        t.LostFocus += (s, e) => p.Invalidate();
        p.Controls.Add(t);
        Controls.Add(p);
        return p;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int one = 1; DwmSetWindowAttribute(Handle, 20, ref one, 4); // dark title bar
        int round = 2; DwmSetWindowAttribute(Handle, 33, ref round, 4);
        int caption = ColorTranslator.ToWin32(UI.Page); DwmSetWindowAttribute(Handle, 35, ref caption, 4);
    }

    protected override void OnShown(EventArgs e) { base.OnShown(e); url.Focus(); }

    void MarkSwatch()
    {
        foreach (var sw in swatches) { sw.Selected = sw.Color.ToArgb() == color.ToArgb(); sw.Invalidate(); }
    }

    static string Normalise(string link)
    {
        link = (link ?? "").Trim();
        if (link.StartsWith("feed://", StringComparison.OrdinalIgnoreCase)) link = "https://" + link.Substring(7);
        if (link != "" && !link.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !link.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) link = "https://" + link;
        return link;
    }

    // Fetches the link on a background thread. With thenAccept, closes the dialog once it works.
    void Check(bool thenAccept)
    {
        var link = Normalise(url.Text);
        if (link == "") { result.ForeColor = UI.Warn; result.Text = "Paste a link first."; url.Focus(); return; }
        Uri tmp;
        if (!Uri.TryCreate(link, UriKind.Absolute, out tmp)) { result.ForeColor = UI.Warn; result.Text = "✗ That doesn't look like a web address."; return; }
        check.Enabled = ok.Enabled = false;
        result.ForeColor = UI.Sub;
        result.Text = "Checking…";
        ThreadPool.QueueUserWorkItem(_ =>
        {
            string msg, title = null, feedUrl = null;
            bool good = false;
            try
            {
                var got = Rss.Fetch(link, 0, out title, out feedUrl);
                if (got.Count == 0) throw new Exception("That feed is empty");
                var newest = got.Where(s => s.When != DateTime.MinValue).Select(s => s.When).DefaultIfEmpty(DateTime.MinValue).Max();
                msg = "✓ " + (string.IsNullOrEmpty(title) ? "Feed found" : title) + "  ·  " + got.Count + " headlines" + (newest != DateTime.MinValue ? ", newest " + FeedsForm.Ago(newest) + (FeedsForm.Ago(newest) == "now" ? "" : " ago") : "");
                if (feedUrl != link) msg += "  ·  found the feed on that page";
                good = true;
            }
            catch (Exception ex) { msg = "✗ " + FeedsForm.Friendly(ex); }
            try
            {
                BeginInvoke((Action)(() =>
                {
                    check.Enabled = ok.Enabled = true;
                    result.Text = msg;
                    result.ForeColor = good ? UI.Good : UI.Warn;
                    if (!good) return;
                    checkedUrl = url.Text.Trim(); foundUrl = feedUrl;
                    if (!nameTyped && !string.IsNullOrEmpty(title)) name.Text = title.Length > 32 ? title.Substring(0, 32).Trim() : title;
                    if (thenAccept) Accept();
                }));
            }
            catch { }
        });
    }

    void Accept()
    {
        var typed = url.Text.Trim();
        bool unchanged = typed == originalUrl && originalUrl != "";
        if (!unchanged && typed != checkedUrl) { Check(true); return; } // check new links before saving them
        var link = unchanged ? originalUrl : foundUrl ?? Normalise(typed);
        var n = name.Text.Trim();
        if (n == "") { Uri u; n = Uri.TryCreate(link, UriKind.Absolute, out u) ? u.Host.Replace("www.", "") : "Feed"; }
        Result = new Source { Name = n, Url = link, Color = color, On = true };
        DialogResult = DialogResult.OK;
        Close();
    }
}

// ======================================================================= Settings backup
// Copies the app's settings (HKCU\Software\<KeyName>) to "<KeyName>-settings.reg" next to the exe, so they survive a
// Windows reinstall when the app folder is kept (on OneDrive, a USB stick…). It's a normal .reg export, so
// double-clicking the file restores the settings as well. A restore runs after the app has closed, so nothing the app
// saves on the way out can overwrite it, and then the app starts again with the restored settings.
// Same code in every Kinetik app.
static class Backup
{
    public static string KeyName, AppTitle;
    public static Action BeforeExit; // e.g. hide the tray icon, as the app's own Exit does
    static bool restoreAfterExit;

    public static string FilePath
    {
        get { return System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.ExecutablePath), KeyName + "-settings.reg"); }
    }
    public static bool Exists { get { return System.IO.File.Exists(FilePath); } }
    public static string SavedText
    {
        get { return Exists ? System.IO.File.GetLastWriteTime(FilePath).ToString("d MMM yyyy, h:mm tt") : "never"; }
    }

    static bool HasSettings
    {
        get
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(@"Software\" + KeyName)) return k != null; }
            catch { return true; }
        }
    }

    static bool Reg(string args) { return KinetikSuite.RegistryFiles.Run(args); }

    static bool FileIsOurs()
    {
        try
        {
            string own = "[HKEY_CURRENT_USER\\Software\\" + KeyName;
            bool any = false;
            foreach (var line in System.IO.File.ReadAllLines(FilePath))
            {
                var l = line.Trim();
                if (!l.StartsWith("[")) continue;
                if (!l.StartsWith(own + "]", StringComparison.OrdinalIgnoreCase) && !l.StartsWith(own + "\\", StringComparison.OrdinalIgnoreCase)) return false;
                any = true;
            }
            return any;
        }
        catch { return false; }
    }

    static bool Import()
    {
        try { return FileIsOurs() && KinetikSuite.RegistryFiles.Replace(FilePath, new[] { KeyName }); }
        catch (Exception ex) { Say(ex.Message, MessageBoxIcon.Warning); return false; }
    }

    static void Say(string text, MessageBoxIcon icon) { MessageBox.Show(text, AppTitle, MessageBoxButtons.OK, icon); }

    // At launch: no settings in the registry (a fresh Windows) but a backup beside the exe, so offer it.
    public static void OfferRestore()
    {
        if (HasSettings || !Exists) return;
        if (MessageBox.Show("There are saved " + AppTitle + " settings in this folder, from " + SavedText + ".\n\nRestore them?",
                AppTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        if (!Import()) Say("Couldn't restore the settings from\n" + FilePath, MessageBoxIcon.Warning);
    }

    public static void SaveNow()
    {
        if (Reg("export \"HKCU\\Software\\" + KeyName + "\" \"" + FilePath + "\" /y"))
            Say("Settings backed up to\n" + FilePath + "\n\nKeep this file with the app. After reinstalling Windows, " + AppTitle + " offers to restore it the first time it starts.", MessageBoxIcon.Information);
        else
            Say("Couldn't save the backup to\n" + FilePath + "\n\nCheck that the folder isn't read-only.", MessageBoxIcon.Warning);
    }

    public static void RestoreNow()
    {
        if (!Exists) { Say("There's no backup yet. Use \"Back up settings\" first.\n\nIt would be saved as\n" + FilePath, MessageBoxIcon.Information); return; }
        if (!FileIsOurs()) { Say("The backup file isn't a " + AppTitle + " settings file, so it wasn't used.", MessageBoxIcon.Warning); return; }
        if (MessageBox.Show("Replace the current settings with the backup from " + SavedText + "?\n\n" + AppTitle + " will restart.",
                AppTitle, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        restoreAfterExit = true;
        if (BeforeExit != null) try { BeforeExit(); } catch { }
        Application.Exit();
    }

    // Called from Main once the app has closed and released its single-instance lock.
    public static void AfterExit()
    {
        if (!restoreAfterExit) return;
        if (!Import()) Say("Couldn't restore the settings from\n" + FilePath, MessageBoxIcon.Warning);
        try { System.Diagnostics.Process.Start(Application.ExecutablePath); } catch { }
    }
}

// ======================================================================= The right-click menu every Kinetik widget shares
// The look, placement, startup and backup options, in the same order and the same words in every Kinetik app, so they
// feel like one set. Each app adds its own items above. Same code in every Kinetik app.
#pragma warning disable 0649 // the hooks an app has no use for stay null
class CommonMenu
{
    public static readonly int[] Scales = { 80, 90, 100, 115, 125, 150 };
    public static readonly int[] Opacities = { 100, 95, 80, 65, 50, 30, 0 };
    public const int DefaultOpacity = 95;
    public static readonly string[] Fonts = { "Segoe UI Variable Text", "Segoe UI", "Bahnschrift", "Calibri", "Aptos", "Inter", "Cascadia Code", "Consolas", "Verdana", "Georgia" };

    // Settings saved before the shared default changed from 92% to 95% move with it; any other choice is kept.
    public static int Migrate(int opacity) { return opacity == 92 ? DefaultOpacity : opacity; }

    public string Title, Version;
    public Action OpenTheme;                                        // opens the theme picker (or the page with it)
    public Func<int> Scale; public Action<int> SetScale;
    public int[] Widths; public Func<int> CardWidth; public Action<int> SetCardWidth; // optional: Narrow, Normal, Wide
    public Func<string> FontName; public Action<string> SetFontName;   // optional; "" = the default font
    public Func<int> Opacity; public Action<int> SetOpacity;
    public Func<int> Layer; public Action<int> SetLayer;
    public Func<bool> Locked; public Action<bool> SetLocked;
    public Func<bool> ClickThrough; public Action<bool> SetClickThrough; // optional
    public Func<bool> Snap; public Action<bool> SetSnap;
    public string LockText = "Lock position";
    public Action OpenSettings;                                     // optional: the app's Settings window
    public Func<bool> RunAtStartup; public Action<bool> SetRunAtStartup;   // optional
    public Action BackUp, Restore; public Func<string> LastBackup; // optional
    public Action Exit;                                         // optional

    static ToolStripMenuItem Item(string text, bool check, Action click)
    {
        var it = new ToolStripMenuItem(text) { Checked = check };
        it.Click += (s, e) => click();
        return it;
    }

    static bool HasFont(string name)
    {
        try { using (var f = new FontFamily(name)) return f.Name == name; } catch { return false; }
    }

    // Appearance and placement: Theme, Size, Font, Background opacity, Placement.
    public void AddLook(ToolStripItemCollection items)
    {
        if (OpenTheme != null) items.Add(Item("Theme…", false, OpenTheme));

        var size = new ToolStripMenuItem("Size");
        foreach (var s in Scales) { int v = s; size.DropDownItems.Add(Item(v + "%", Scale() == v, () => SetScale(v))); }
        if (Widths != null)
        {
            size.DropDownItems.Add(new ToolStripSeparator());
            string[] names = { "Narrow", "Normal", "Wide" };
            for (int i = 0; i < Widths.Length && i < 3; i++) { int v = Widths[i]; size.DropDownItems.Add(Item(names[i], CardWidth() == v, () => SetCardWidth(v))); }
        }
        items.Add(size);

        if (FontName != null)
        {
            var font = new ToolStripMenuItem("Font");
            font.DropDownItems.Add(Item("Default", FontName() == "", () => SetFontName("")));
            foreach (var n in Fonts.Where(HasFont)) { string v = n; font.DropDownItems.Add(Item(v, FontName() == v, () => SetFontName(v))); }
            if (FontName() != "" && !Fonts.Contains(FontName())) font.DropDownItems.Add(Item(FontName(), true, () => { }));
            items.Add(font);
        }

        var opacity = new ToolStripMenuItem("Background opacity");
        foreach (var o in Opacities) { int v = o; opacity.DropDownItems.Add(Item(v + "%", Opacity() == v, () => SetOpacity(v))); }
        if (!Opacities.Contains(Opacity())) opacity.DropDownItems.Add(Item(Opacity() + "%", true, () => { }));
        items.Add(opacity);

        var place = new ToolStripMenuItem("Placement");
        string[] layers = { "On the desktop", "Normal window", "Always on top" };
        for (int i = 0; i < 3; i++) { int v = i; place.DropDownItems.Add(Item(layers[i], Layer() == v, () => SetLayer(v))); }
        place.DropDownItems.Add(new ToolStripSeparator());
        place.DropDownItems.Add(Item(LockText, Locked(), () => SetLocked(!Locked())));
        if (ClickThrough != null) place.DropDownItems.Add(Item("Click-through when locked", ClickThrough(), () => SetClickThrough(!ClickThrough())));
        place.DropDownItems.Add(Item("Snap to screen edges", Snap(), () => SetSnap(!Snap())));
        items.Add(place);
    }

    // The rest: Settings…, Run at startup, Backup, then the version and Exit.
    public void AddEnd(ToolStripItemCollection items)
    {
        items.Add(new ToolStripSeparator());
        if (OpenSettings != null) items.Add(Item("Settings…", false, OpenSettings));
        if (RunAtStartup != null) items.Add(Item("Run at startup", RunAtStartup(), () => SetRunAtStartup(!RunAtStartup())));
        if (BackUp != null)
        {
            var backup = new ToolStripMenuItem("Backup");
            backup.DropDownItems.Add(Item("Back up settings", false, BackUp));
            backup.DropDownItems.Add(Item("Restore settings…", false, Restore));
            backup.DropDownItems.Add(new ToolStripMenuItem("Last backup: " + LastBackup()) { Enabled = false });
            items.Add(backup);
        }
        items.Add(new ToolStripSeparator());
        items.Add(new ToolStripMenuItem(Title + " " + Version) { Enabled = false });
        if (Exit != null) items.Add(Item("Exit", false, Exit));
    }

    public void AddAll(ToolStripItemCollection items) { AddLook(items); AddEnd(items); }
}
#pragma warning restore 0649
