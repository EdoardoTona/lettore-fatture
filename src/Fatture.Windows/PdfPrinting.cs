using System.Drawing.Printing;
using Windows.Data.Pdf;
using Windows.Storage.Streams;

namespace Fatture.Windows;

internal static class PdfPrinting
{
    public static Task PrintAsync(byte[] pdf, PrintDocument job, CancellationToken cancellation) => Task.Run(() =>
    {
        // A separate MTA worker owns the print PDF. The UI and its preview remain
        // responsive; only one print-resolution page is held in memory at a time.
        using var file = new MemoryStream(pdf, writable: false);
        using var stream = file.AsRandomAccessStream();
        var document = PdfDocument.LoadFromStreamAsync(stream).AsTask().GetAwaiter().GetResult();
        var first = job.PrinterSettings.PrintRange == PrintRange.SomePages ? job.PrinterSettings.FromPage - 1 : 0;
        var last = job.PrinterSettings.PrintRange == PrintRange.SomePages ? job.PrinterSettings.ToPage : (int)document.PageCount;
        int index = Math.Clamp(first, 0, (int)document.PageCount - 1);
        last = Math.Clamp(last, index + 1, (int)document.PageCount);
        job.PrintController = new StandardPrintController();
        job.PrintPage += (_, e) =>
        {
            if (cancellation.IsCancellationRequested) { e.Cancel = true; return; }
            using var page = document.GetPage((uint)index);
            using var output = new InMemoryRandomAccessStream();
            var scale = Math.Min(3, 3500 / Math.Max(page.Size.Width, page.Size.Height));
            page.RenderToStreamAsync(output, new PdfPageRenderOptions
            {
                DestinationWidth = (uint)Math.Max(1, page.Size.Width * scale),
                DestinationHeight = (uint)Math.Max(1, page.Size.Height * scale)
            }).AsTask().GetAwaiter().GetResult();
            output.Seek(0);
            using var managed = output.AsStreamForRead();
            using var image = Image.FromStream(managed);
            var bounds = e.MarginBounds;
            var fit = Math.Min(bounds.Width / (float)image.Width, bounds.Height / (float)image.Height);
            var width = image.Width * fit; var height = image.Height * fit;
            e.Graphics!.DrawImage(image, bounds.Left + (bounds.Width - width) / 2, bounds.Top, width, height);
            e.HasMorePages = ++index < last;
        };
        cancellation.ThrowIfCancellationRequested();
        job.Print();
    }, cancellation);
}
