using Windows.Data.Pdf;
using Windows.Storage.Streams;

namespace Fatture.Windows;

// Windows' PDF renderer, with only visible pages rasterized and a bounded cache.
// The original PDF is always used when saving or opening in another PDF reader.
internal sealed class NativePdfView : ScrollableControl
{
    private sealed class Source(FileStream file, IRandomAccessStream stream, PdfDocument document) : IDisposable
    {
        public PdfDocument Document { get; } = document;
        public void Dispose() { stream.Dispose(); file.Dispose(); }
    }

    private readonly SemaphoreSlim native = new(1);
    private readonly Dictionary<int, Bitmap> cache = [];
    private readonly HashSet<int> failed = [];
    private readonly System.Windows.Forms.Timer resizeTimer = new() { Interval = 150 };
    private Source? source;
    private SizeF[] sizes = [];
    private Rectangle[] pages = [];
    private long cacheBytes;
    private int generation;
    private bool rendering;
    private bool renderAgain;
    private bool closing;
    private bool fitWidth = true;
    private double zoom = 1;
    private string message = "Trascina qui XML, P7M, EML o PDF\noppure scegli Apri documenti";
    private const long CacheLimit = 32 * 1024 * 1024;
    public event Action? PageChanged;
    public event Action<string>? PreviewFailed;
    public int PageCount => pages.Length;
    public int CurrentPage => pages.Length == 0 ? 0 : Math.Max(0,
        Array.FindIndex(pages, page => page.Bottom + AutoScrollPosition.Y > 0)) + 1;
    public bool HasRenderedPage => cache.Count > 0;

    public NativePdfView()
    {
        DoubleBuffered = true;
        AutoScroll = true;
        BackColor = Color.FromArgb(233, 235, 238);
        TabStop = true;
        resizeTimer.Tick += (_, _) => { resizeTimer.Stop(); LayoutPages(); };
    }

    public async Task OpenAsync(string? path)
    {
        var version = ++generation;
        await native.WaitAsync();
        try
        {
            if (version != generation || closing) return;
            source?.Dispose(); source = null;
            ClearCache(); sizes = []; pages = []; AutoScrollMinSize = Size.Empty;
            AutoScrollPosition = Point.Empty;
            message = path == null ? "Trascina qui XML, P7M, EML o PDF\noppure scegli Apri documenti" : "Caricamento PDF…";
            Invalidate();
            if (path == null) { PageChanged?.Invoke(); return; }
            var file = File.OpenRead(path);
            IRandomAccessStream? stream = null;
            try
            {
                stream = file.AsRandomAccessStream();
                var document = await PdfDocument.LoadFromStreamAsync(stream);
                if (version != generation || closing) { stream.Dispose(); file.Dispose(); return; }
                if (document.PageCount == 0 || document.PageCount > 5000)
                    throw new InvalidDataException("Il PDF è vuoto o supera il limite di 5000 pagine.");
                sizes = new SizeF[document.PageCount];
                for (uint i = 0; i < document.PageCount; i++)
                {
                    using var page = document.GetPage(i);
                    if (page.Size.Width <= 0 || page.Size.Height <= 0 || !double.IsFinite(page.Size.Width + page.Size.Height))
                        throw new InvalidDataException("Dimensioni pagina PDF non valide.");
                    sizes[i] = new((float)page.Size.Width, (float)page.Size.Height);
                }
                source = new(file, stream, document);
                fitWidth = true; zoom = 1; message = "";
                LayoutPages();
            }
            catch { stream?.Dispose(); file.Dispose(); sizes = []; throw; }
        }
        catch
        {
            message = "PDF non leggibile\nIl documento potrebbe essere danneggiato o protetto da password.";
            Invalidate(); throw;
        }
        finally { native.Release(); }
        await RenderVisibleAsync();
    }

    public void Fit() { fitWidth = true; LayoutPages(); }
    public void ZoomBy(double factor)
    {
        if (pages.Length == 0) return;
        zoom = Math.Clamp(pages[0].Width / sizes[0].Width * factor, 0.25, 4);
        fitWidth = false; LayoutPages();
    }
    public void GoToPage(int number)
    {
        if (pages.Length == 0) return;
        var page = pages[Math.Clamp(number - 1, 0, pages.Length - 1)];
        AutoScrollPosition = new(0, Math.Max(0, page.Top - 18));
        Invalidate(); PageChanged?.Invoke();
    }

    private void LayoutPages()
    {
        if (closing || sizes.Length == 0) return;
        var current = Math.Max(0, CurrentPage - 1);
        generation++; ClearCache();
        var available = Math.Max(160, ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 48);
        var width = fitWidth ? Math.Min(1600, available) : (int)(sizes[0].Width * zoom);
        var scale = width / sizes[0].Width;
        var widest = sizes.Max(size => Math.Min(8000, (int)(size.Width * scale)));
        var canvas = Math.Max(ClientSize.Width - SystemInformation.VerticalScrollBarWidth, widest + 48);
        pages = new Rectangle[sizes.Length];
        var y = 24;
        for (int i = 0; i < sizes.Length; i++)
        {
            var w = Math.Clamp((int)(sizes[i].Width * scale), 1, 8000);
            var h = Math.Clamp((int)(sizes[i].Height * scale), 1, 12000);
            pages[i] = new((canvas - w) / 2, y, w, h);
            y += h + 24;
        }
        AutoScrollMinSize = new(widest + 48, y);
        AutoScrollPosition = new(0, Math.Max(0, pages[current].Top - 24));
        Invalidate(); PageChanged?.Invoke();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (fitWidth) { resizeTimer.Stop(); resizeTimer.Start(); }
    }
    protected override void OnScroll(ScrollEventArgs se) { base.OnScroll(se); Invalidate(); PageChanged?.Invoke(); }
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if ((ModifierKeys & Keys.Control) != 0) ZoomBy(e.Delta > 0 ? 1.15 : 1 / 1.15);
        else base.OnMouseWheel(e);
        Invalidate(); PageChanged?.Invoke();
    }
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.PageDown or Keys.PageUp or Keys.Home or Keys.End || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.PageDown) { GoToPage(CurrentPage + 1); e.Handled = true; }
        if (e.KeyCode == Keys.PageUp) { GoToPage(CurrentPage - 1); e.Handled = true; }
        if (e.Control && e.KeyCode == Keys.Home) { GoToPage(1); e.Handled = true; }
        if (e.Control && e.KeyCode == Keys.End) { GoToPage(PageCount); e.Handled = true; }
        base.OnKeyDown(e);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (pages.Length == 0)
        {
            TextRenderer.DrawText(e.Graphics, message, Font, ClientRectangle, Color.DimGray,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            return;
        }
        for (int i = 0; i < pages.Length; i++)
        {
            var rect = pages[i]; rect.Offset(AutoScrollPosition);
            if (!rect.IntersectsWith(ClientRectangle)) continue;
            e.Graphics.FillRectangle(Brushes.White, rect);
            if (cache.TryGetValue(i, out var bitmap)) e.Graphics.DrawImage(bitmap, rect);
            else TextRenderer.DrawText(e.Graphics, failed.Contains(i) ? "Pagina non leggibile" : "Caricamento…",
                Font, rect, Color.Gray, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            e.Graphics.DrawRectangle(Pens.LightGray, rect);
        }
        _ = RenderVisibleAsync();
    }

    private async Task RenderVisibleAsync()
    {
        if (rendering) { renderAgain = true; return; }
        rendering = true;
        try
        {
            do
            {
                renderAgain = false;
                var version = generation;
                var viewport = new Rectangle(-AutoScrollPosition.X, -AutoScrollPosition.Y, ClientSize.Width, ClientSize.Height);
                var visible = Enumerable.Range(0, pages.Length).Where(i => pages[i].IntersectsWith(viewport)).ToArray();
                foreach (var index in visible)
                {
                    if (closing || version != generation) break;
                    if (cache.ContainsKey(index) || failed.Contains(index)) continue;
                    await native.WaitAsync();
                    try
                    {
                        if (closing || version != generation || source == null) break;
                        var target = pages[index];
                        using var page = source.Document.GetPage((uint)index);
                        using var output = new InMemoryRandomAccessStream();
                        // Cap raster size; scrolling large documents never renders all pages.
                        var scale = Math.Min(1, 2400d / Math.Max(target.Width, target.Height));
                        await page.RenderToStreamAsync(output, new PdfPageRenderOptions
                        {
                            DestinationWidth = (uint)Math.Max(1, target.Width * scale),
                            DestinationHeight = (uint)Math.Max(1, target.Height * scale)
                        });
                        if (closing || version != generation) break;
                        output.Seek(0);
                        using var managed = output.AsStreamForRead();
                        using var image = Image.FromStream(managed);
                        var bitmap = new Bitmap(image);
                        cache[index] = bitmap; cacheBytes += (long)bitmap.Width * bitmap.Height * 4;
                        foreach (var key in cache.Keys.Where(key => key != index && !visible.Contains(key)).ToArray())
                        {
                            if (cacheBytes <= CacheLimit) break;
                            cacheBytes -= (long)cache[key].Width * cache[key].Height * 4;
                            cache[key].Dispose(); cache.Remove(key);
                        }
                        Invalidate();
                    }
                    catch (Exception error)
                    {
                        if (!closing && version == generation)
                        {
                            failed.Add(index); Invalidate(); PreviewFailed?.Invoke(error.Message);
                        }
                    }
                    finally { native.Release(); }
                }
            } while (renderAgain && !closing);
        }
        finally { rendering = false; }
    }

    private void ClearCache()
    {
        foreach (var bitmap in cache.Values) bitmap.Dispose();
        cache.Clear(); failed.Clear(); cacheBytes = 0;
    }
    public async Task CloseAsync()
    {
        closing = true; generation++; resizeTimer.Stop();
        await native.WaitAsync();
        try { source?.Dispose(); source = null; ClearCache(); }
        finally { native.Release(); }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !closing)
        {
            closing = true; generation++; resizeTimer.Dispose(); ClearCache();
            var old = source; source = null;
            _ = ReleaseAsync(old);
        }
        base.Dispose(disposing);
    }
    private async Task ReleaseAsync(Source? old)
    {
        await native.WaitAsync().ConfigureAwait(false);
        try { old?.Dispose(); } finally { native.Release(); }
    }
}
