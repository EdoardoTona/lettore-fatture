using Fatture.Core;

namespace Fatture.Windows;

internal sealed record OpenDocument(RenderedDocument Document, string Input)
{
    public override string ToString() => Document.Title;
}

internal sealed class DocumentList : ListBox
{
    public event Action<int>? CloseRequested;
    public event Action? DeleteRequested;

    public DocumentList()
    {
        DrawMode = DrawMode.OwnerDrawFixed;
        ItemHeight = 58;
        IntegralHeight = false;
        SelectionMode = SelectionMode.MultiExtended;
        BorderStyle = BorderStyle.None;
        BackColor = Color.FromArgb(246, 247, 249);
        AccessibleName = "Documenti aperti";
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || Items[e.Index] is not OpenDocument entry) return;
        var selected = (e.State & DrawItemState.Selected) != 0;
        using var background = new SolidBrush(selected ? Color.FromArgb(219, 235, 238) : BackColor);
        e.Graphics.FillRectangle(background, e.Bounds);
        var title = new Rectangle(e.Bounds.Left + 14, e.Bounds.Top + 10, e.Bounds.Width - 52, 21);
        var subtitle = new Rectangle(title.Left, title.Top + 23, title.Width, 18);
        TextRenderer.DrawText(e.Graphics, entry.Document.Title, Font, title, Color.FromArgb(32, 39, 46),
            TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        using var small = new Font(Font.FontFamily, Math.Max(7, Font.Size - 1));
        TextRenderer.DrawText(e.Graphics, entry.Document.Subtitle, small, subtitle, Color.DimGray,
            TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        TextRenderer.DrawText(e.Graphics, "×", Font, CloseBounds(e.Bounds), Color.DimGray,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        if ((e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
    }
    private static Rectangle CloseBounds(Rectangle row) => new(row.Right - 34, row.Top + 12, 28, row.Height - 24);
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
}
