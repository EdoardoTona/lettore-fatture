using Fatture.Core;

namespace Fatture.Windows;

internal sealed record OpenDocument(RenderedDocument Document, string Input)
{
    public override string ToString() => Document.Title;
}

internal sealed class DocumentList : ListBox
{
    private const TextFormatFlags TextFlags = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;
    private readonly ToolTip tooltip = new() { InitialDelay = 500, AutoPopDelay = 10_000 };
    private Font? subtitleFont;
    private int titleHeight, subtitleHeight;
    public event Action<int>? CloseRequested;
    public event Action? DeleteRequested;

    public DocumentList()
    {
        DrawMode = DrawMode.OwnerDrawFixed;
        IntegralHeight = false;
        SelectionMode = SelectionMode.MultiExtended;
        BorderStyle = BorderStyle.None;
        BackColor = Color.FromArgb(246, 247, 249);
        AccessibleName = "Documenti aperti";
        UpdateMetrics();
    }

    private void UpdateMetrics()
    {
        if (IsDisposed || Disposing) return;
        subtitleFont?.Dispose();
        subtitleFont = new Font(Font.FontFamily, Math.Max(7, Font.SizeInPoints - 1), FontStyle.Regular, GraphicsUnit.Point);
        // Owner-drawn rectangles are physical pixels, while fonts follow the
        // monitor's DPI. Measure both lines instead of fixing their pixel height.
        if (IsHandleCreated)
        {
            using var graphics = CreateGraphics();
            titleHeight = TextRenderer.MeasureText(graphics, "Ag", Font, Size.Empty, TextFlags).Height;
            subtitleHeight = TextRenderer.MeasureText(graphics, "Ag", subtitleFont, Size.Empty, TextFlags).Height;
        }
        else
        {
            titleHeight = (int)Math.Ceiling(Font.GetHeight(DeviceDpi));
            subtitleHeight = (int)Math.Ceiling(subtitleFont.GetHeight(DeviceDpi));
        }
        ItemHeight = Math.Min(255, Math.Max(LogicalToDeviceUnits(58),
            titleHeight + subtitleHeight + LogicalToDeviceUnits(24)));
        Invalidate();
    }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); UpdateMetrics(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); UpdateMetrics(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); UpdateMetrics(); }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || Items[e.Index] is not OpenDocument entry) return;
        var selected = (e.State & DrawItemState.Selected) != 0;
        using var background = new SolidBrush(selected ? Color.FromArgb(219, 235, 238) : BackColor);
        e.Graphics.FillRectangle(background, e.Bounds);
        var close = CloseBounds(e.Bounds);
        var left = e.Bounds.Left + LogicalToDeviceUnits(14);
        var gap = LogicalToDeviceUnits(4);
        var top = e.Bounds.Top + Math.Max(0, (e.Bounds.Height - titleHeight - gap - subtitleHeight) / 2);
        var title = new Rectangle(left, top, Math.Max(0, close.Left - LogicalToDeviceUnits(6) - left), titleHeight);
        var subtitle = new Rectangle(left, title.Bottom + gap, title.Width, subtitleHeight);
        TextRenderer.DrawText(e.Graphics, entry.Document.Title, Font, title, Color.FromArgb(32, 39, 46),
            TextFlags);
        TextRenderer.DrawText(e.Graphics, entry.Document.Subtitle, subtitleFont, subtitle, Color.DimGray, TextFlags);
        TextRenderer.DrawText(e.Graphics, "×", Font, close, Color.DimGray,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        if ((e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
    }
    private Rectangle CloseBounds(Rectangle row)
    {
        var size = Math.Min(LogicalToDeviceUnits(28), row.Height);
        return new(row.Right - LogicalToDeviceUnits(8) - size, row.Top + (row.Height - size) / 2, size, size);
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var index = IndexFromPoint(e.Location);
        var text = index >= 0 && Items[index] is OpenDocument entry
            ? entry.Document.Title + "\n" + entry.Document.Subtitle : "";
        if (tooltip.GetToolTip(this) != text) tooltip.SetToolTip(this, text);
    }
    protected override void OnMouseLeave(EventArgs e) { tooltip.SetToolTip(this, ""); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        var index = IndexFromPoint(e.Location);
        if (e.Button == MouseButtons.Left && index >= 0 && CloseBounds(GetItemRectangle(index)).Contains(e.Location))
        {
            CloseRequested?.Invoke(index); return;
        }
        if (e.Button == MouseButtons.Right && index >= 0 && !SelectedIndices.Contains(index)) SelectedIndex = index;
        base.OnMouseDown(e);
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Delete) { DeleteRequested?.Invoke(); e.Handled = true; }
        if (e.Control && e.KeyCode == Keys.A)
        {
            for (int i = 0; i < Items.Count; i++) SetSelected(i, true);
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { subtitleFont?.Dispose(); tooltip.Dispose(); }
        base.Dispose(disposing);
    }
}
