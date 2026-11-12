using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Text;

// Compile alongside the app with /main:FeedsLayoutTests; no window or saved settings are used.
static class FeedsLayoutTests
{
    static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); }

    static void Set(object target, string name, object value)
    { target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).SetValue(target, value); }

    static byte[] Thumbnail()
    {
        using (var bmp = new Bitmap(168, 112))
        using (var g = Graphics.FromImage(bmp))
        using (var stream = new MemoryStream())
        {
            g.Clear(Color.SteelBlue);
            using (var brush = new SolidBrush(Color.LightSkyBlue)) g.FillEllipse(brush, 80, 12, 50, 50);
            bmp.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }
    }

    static void Main(string[] args)
    {
        Program.Demo = true;
        string title;
        var rss = Rss.Parse("<rss xmlns:media='http://search.yahoo.com/mrss/'><channel><title>Test</title>"
            + "<item><title>Media</title><media:group><media:thumbnail url='https://example.com/a.jpg'/></media:group></item>"
            + "<item><title>Enclosure</title><enclosure type='image/jpeg' url='/b.jpg'/></item>"
            + "<item><title>HTML</title><description><![CDATA[<img src='https://example.com/c.jpg?a=1&amp;b=2'>]]></description></item>"
            + "<item><title>No image</title><enclosure type='audio/mpeg' url='https://example.com/a.mp3'/></item>"
            + "</channel></rss>", 0, out title);
        Check(title == "Test" && rss.Count == 4, "RSS headlines remain intact");
        Check(rss[0].ImageUrl == "https://example.com/a.jpg" && rss[1].ImageUrl == "/b.jpg", "Media and image enclosures");
        Check(rss[2].ImageUrl == "https://example.com/c.jpg?a=1&b=2" && rss[3].ImageUrl == null, "HTML images and audio exclusion");
        var atom = Rss.Parse("<feed xmlns='http://www.w3.org/2005/Atom'><entry><title>Atom</title><link href='https://example.com/story'/><link rel='enclosure' type='image/png' href='https://example.com/image.png'/></entry></feed>", 1, out title);
        Check(atom[0].ImageUrl == "https://example.com/image.png" && atom[0].Link == "https://example.com/story", "Atom enclosure preserves article link");
        var invalid = new Story { ImageUrl = "file:///missing.png", Title = "Still readable" };
        Rss.LoadThumbnail(invalid, "https://example.com/feed", CancellationToken.None);
        Check(invalid.Thumbnail == null && invalid.Title == "Still readable", "Unsupported image falls back to text");

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = new Thread(() =>
        {
            using (var client = listener.AcceptTcpClient())
            using (var stream = client.GetStream())
            using (var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true))
            {
                while (!string.IsNullOrEmpty(reader.ReadLine())) { }
                var body = Thumbnail();
                var header = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
                stream.Write(header, 0, header.Length); stream.Write(body, 0, body.Length);
            }
        }) { IsBackground = true };
        server.Start();
        try
        {
            var local = new Story { ImageUrl = "/image.png" };
            Rss.LoadThumbnail(local, "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/rss", CancellationToken.None);
            Check(local.Thumbnail != null, "Relative feed image downloads and decodes");
            using (var stream = new MemoryStream(local.Thumbnail))
            using (var image = Image.FromStream(stream)) Check(image.Width == 168 && image.Height == 112, "Thumbnail is bounded to display size");
        }
        finally { listener.Stop(); server.Join(4000); }

        var app = (FeedsForm)FormatterServices.GetUninitializedObject(typeof(FeedsForm));
        var cfg = DemoData.Settings(); cfg.Shadow = false; cfg.Title = "Technology and science headlines from around the world";
        Set(app, "Cfg", cfg);
        Set(app, "Errors", new Dictionary<int, string>());
        var stories = cfg.List.SelectMany((s, i) => DemoData.Stories(s.Url, i)).OrderByDescending(s => s.When).ToList();
        stories[0].Thumbnail = Thumbnail();
        Set(app, "stories", stories);
        app.FetchedAt = DateTime.Now;
        int count = 0;
        foreach (var theme in new[] { "Midnight", "Light", "Terminal" })
        foreach (int width in new[] { 120, 240, 340, 480 })
        foreach (float scale in new[] { 1f, 1.5f, 2f })
        foreach (bool detail in new[] { true, false })
        {
            cfg.ThemeName = theme; cfg.Width = width; cfg.ShowSource = detail; cfg.TwoLines = detail; cfg.ShowHeader = detail;
            var frame = app.Compose(scale, scale, 0, -1, (int)(800 * scale), (w, h) => new Bitmap(w, h));
            using (frame.Bmp)
            {
                Check(frame.Hits.Count == cfg.MaxItems, "Every story has a hit target");
                Check(frame.MaxScroll >= 0 && frame.WinW == (int)(width * scale) + frame.Margin * 2, "Scaled layout dimensions");
                Check(frame.Hits.Where(r => r.Width > 0 && r.Height > 0).All(r => r.Left >= frame.Margin && r.Right <= frame.WinW - frame.Margin), "Click targets stay inside card");
                if (args.Length > 0 && width == 340 && scale == 1 && detail)
                {
                    Directory.CreateDirectory(args[0]);
                    frame.Bmp.Save(Path.Combine(args[0], "feeds-" + theme + ".png"));
                }
            }
            var scrolled = app.Compose(scale, scale, frame.MaxScroll, 2, (int)(800 * scale), (w, h) => new Bitmap(w, h));
            using (scrolled.Bmp) Check(scrolled.Hits.Last().Height > 0, "Last story is reachable by scrolling");
            count++;
        }
        Set(app, "stories", new List<Story>());
        var empty = app.Compose(1, 1, 0, -1, 800, (w, h) => new Bitmap(w, h));
        using (empty.Bmp) Check(empty.Hits.Count == 0, "Empty feed has no story targets");
        Console.WriteLine("PASS: RSS/Atom images, text fallback, empty state and " + count + " layout combinations with scrolling.");
        // This instance deliberately bypasses the Form constructor and owns no native resources.
        GC.SuppressFinalize(app);
    }
}
