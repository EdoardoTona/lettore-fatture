using System.Diagnostics;
using System.Drawing.Printing;
using System.Globalization;
using Fatture.Core;

namespace Fatture.Windows;

internal sealed partial class MainWindow : Form
{
    private readonly CoreBridge bridge = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly DocumentList documents = new() { Dock = DockStyle.Fill };
    private readonly NativePdfView pdf = new() { Dock = DockStyle.Fill };
    private readonly SplitContainer details = new() { Dock = DockStyle.Fill, Size = new(980, 700), SplitterWidth = 1 };
    private readonly FlowLayoutPanel inspector = new() { Dock = DockStyle.Fill, AutoScroll = true,
        FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Color.White, Padding = new(20) };
    private readonly ToolStripButton save = new("Salva PDF"), excel = new("Excel"), print = new("Stampa"),
        info = new("Informazioni") { CheckOnClick = true };
    private readonly ToolStripStatusLabel status = new("Nessun documento"), pages = new("");
    private readonly ToolStripProgressBar progress = new() { Style = ProgressBarStyle.Marquee, Visible = false, Width = 80 };
    private readonly ToolStripButton closeSelection = new("Chiudi selezionati") { Visible = false };
    private readonly Label notice = new() { Dock = DockStyle.Top, AutoSize = false, Height = 40, Visible = false,
        Padding = new(14, 10, 14, 0), ForeColor = Color.FromArgb(81, 87, 95), BackColor = Color.FromArgb(247, 248, 249) };
    private readonly ToolTip tips = new();
    private readonly HashSet<string> pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly string sessionDirectory = Path.Combine(Path.GetTempPath(), "LettoreFatture-" + Guid.NewGuid().ToString("N"));
    private readonly string[] arguments;
    private bool closing, shutdownComplete, exporting, printing;
    private string? previewId;
    private string? loadedPreviewId;
    private Task previewTask = Task.CompletedTask;
    internal int ExitCode { get; private set; }
    private OpenDocument? Active => documents.SelectedItem as OpenDocument;
    private OpenDocument[] Entries => documents.Items.Cast<OpenDocument>().ToArray();

    public MainWindow(string[] args)
    {
        arguments = args;
        Text = "Lettore Fatture";
        Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        ClientSize = new(1280, 820); MinimumSize = new(920, 580);
        StartPosition = FormStartPosition.CenterScreen; BackColor = Color.White;
        AutoScaleMode = AutoScaleMode.Dpi;
        Directory.CreateDirectory(sessionDirectory);
        BuildLayout(); BuildMenus(); WireEvents(); UpdateActions();
        Shown += async (_, _) =>
        {
            if (arguments.Contains("--smoke-test")) { await SmokeTestAsync(); return; }
            await OpenFilesAsync(arguments.Where(path => !path.StartsWith("--", StringComparison.Ordinal)));
        };
    }

    private void BuildLayout()
    {
        var split = new SplitContainer { Dock = DockStyle.Fill, Size = ClientSize, SplitterDistance = 260,
            Panel1MinSize = 210, Panel2MinSize = 450, SplitterWidth = 1, BackColor = Color.FromArgb(222, 225, 229) };
        var sidebar = new Panel { Dock = DockStyle.Fill, BackColor = documents.BackColor };
        var heading = new Label { Text = "Documenti", Dock = DockStyle.Top, Height = 50,
            Padding = new(16, 18, 0, 0), Font = new(Font, FontStyle.Bold) };
        sidebar.Controls.Add(documents); sidebar.Controls.Add(heading);
        split.Panel1.Controls.Add(sidebar);
        details.Panel1MinSize = 350; details.Panel2MinSize = 240;
        details.SplitterDistance = 690; details.Panel2Collapsed = true;
        var center = new Panel { Dock = DockStyle.Fill };
        center.Controls.Add(pdf); center.Controls.Add(notice);
        var navigation = new ToolStrip { Dock = DockStyle.Bottom, GripStyle = ToolStripGripStyle.Hidden,
            BackColor = Color.White, Padding = new(10, 4, 10, 4) };
        navigation.Items.Add(Button("↑", "Pagina precedente", () => pdf.GoToPage(pdf.CurrentPage - 1)));
        navigation.Items.Add(Button("↓", "Pagina successiva", () => pdf.GoToPage(pdf.CurrentPage + 1)));
        navigation.Items.Add(new ToolStripLabel(""));
        navigation.Items.Add(Button("−", "Riduci (Ctrl + rotella)", () => pdf.ZoomBy(1 / 1.15)));
        navigation.Items.Add(Button("+", "Ingrandisci (Ctrl + rotella)", () => pdf.ZoomBy(1.15)));
        navigation.Items.Add(Button("Adatta", "Adatta alla larghezza", pdf.Fit));
        center.Controls.Add(navigation);
        details.Panel1.Controls.Add(center); details.Panel2.Controls.Add(inspector);
        split.Panel2.Controls.Add(details); Controls.Add(split);

        var toolbar = new ToolStrip { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden,
            BackColor = Color.White, Padding = new(10, 8, 10, 8), ImageScalingSize = new(20, 20) };
        toolbar.Items.Add(Button("Apri…", "Apri documenti (Ctrl+O)", OpenPanel));
        toolbar.Items.Add(new ToolStripSeparator()); toolbar.Items.Add(excel); toolbar.Items.Add(save); toolbar.Items.Add(print);
        toolbar.Items.Add(info); info.Alignment = ToolStripItemAlignment.Right;
        excel.ToolTipText = "Esporta tutte le fatture aperte in Excel (Ctrl+Maiusc+E)";
        save.ToolTipText = "Salva il PDF selezionato (Ctrl+S)";
        info.ToolTipText = "Mostra o nasconde informazioni e firme (Ctrl+Alt+I)";
        Controls.Add(toolbar);
        var footer = new StatusStrip { BackColor = Color.White, SizingGrip = true };
        status.Spring = true; status.TextAlign = ContentAlignment.MiddleLeft;
        footer.Items.AddRange([status, closeSelection, pages, progress]); Controls.Add(footer);
    }

    private static ToolStripButton Button(string text, string tooltip, Action action)
    {
        var button = new ToolStripButton(text) { ToolTipText = tooltip, Padding = new(5, 2, 5, 2) };
        button.Click += (_, _) => action(); return button;
    }

    private void BuildMenus()
    {
        var menu = new MenuStrip { BackColor = Color.White };
        var file = new ToolStripMenuItem("File");
        void Add(ToolStripMenuItem parent, string title, Keys keys, Action action)
        {
            var item = new ToolStripMenuItem(title) { ShortcutKeys = keys };
            item.Click += (_, _) => action(); parent.DropDownItems.Add(item);
        }
        Add(file, "Apri…", Keys.Control | Keys.O, OpenPanel);
        Add(file, "Salva PDF…", Keys.Control | Keys.S, SavePdf);
        Add(file, "Esporta fatture in Excel…", Keys.Control | Keys.Shift | Keys.E, ExportExcel);
        Add(file, "Stampa…", Keys.Control | Keys.P, PrintPdf);
        file.DropDownItems.Add(new ToolStripSeparator());
        Add(file, "Chiudi selezionati", Keys.Control | Keys.W, CloseSelected);
        Add(file, "Chiudi tutti i documenti", Keys.Control | Keys.Shift | Keys.W, () => Remove(Entries));
        file.DropDownItems.Add(new ToolStripSeparator());
        Add(file, "Associa estensioni…", Keys.None, RegisterFiles);
        Add(file, "Esci", Keys.Alt | Keys.F4, Close);
        var view = new ToolStripMenuItem("Vista");
        Add(view, "Mostra / nascondi informazioni", Keys.Control | Keys.Alt | Keys.I, () => info.PerformClick());
        Add(view, "Adatta pagina", Keys.Control | Keys.D0, pdf.Fit);
        Add(view, "Apri PDF nel lettore di sistema", Keys.None, OpenExternal);
        menu.Items.AddRange([file, view]); Controls.Add(menu); MainMenuStrip = menu;
        var context = new ContextMenuStrip();
        context.Items.Add("Chiudi selezionati", null, (_, _) => CloseSelected());
        documents.ContextMenuStrip = context;
    }

    private void WireEvents()
    {
        save.Click += (_, _) => SavePdf(); excel.Click += (_, _) => ExportExcel(); print.Click += (_, _) => PrintPdf();
        info.CheckedChanged += (_, _) => { details.Panel2Collapsed = !info.Checked; UpdateInspector(); };
        closeSelection.Click += (_, _) => CloseSelected();
        documents.CloseRequested += index => Remove(documents.SelectedIndices.Contains(index)
            ? documents.SelectedItems.Cast<OpenDocument>().ToArray() : [(OpenDocument)documents.Items[index]]);
        documents.DeleteRequested += CloseSelected;
        documents.SelectedIndexChanged += (_, _) => { previewTask = SelectDocumentAsync(); };
        inspector.SizeChanged += (_, _) => ResizeInspector();
        pdf.PageChanged += () => pages.Text = pdf.PageCount > 0 ? $"Pagina {pdf.CurrentPage} / {pdf.PageCount}" : "";
        pdf.PreviewFailed += message => ShowNotice("Anteprima PDF: " + message);
        EnableDrop(this);
    }

    private void EnableDrop(Control control)
    {
        control.AllowDrop = true;
        control.DragEnter += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths && paths.Any(Supported)) e.Effect = DragDropEffects.Copy;
        };
        control.DragDrop += async (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths) await OpenFilesAsync(paths.Where(Supported));
        };
        foreach (Control child in control.Controls) EnableDrop(child);
    }
    private static bool Supported(string path) => Path.GetExtension(path).ToLowerInvariant() is ".xml" or ".p7m" or ".eml" or ".pdf";

    private async void OpenPanel()
    {
        using var panel = new OpenFileDialog { Multiselect = true, Title = "Apri documenti",
            Filter = "Documenti supportati|*.xml;*.p7m;*.eml;*.pdf|Tutti i file|*.*" };
        if (panel.ShowDialog(this) == DialogResult.OK) await OpenFilesAsync(panel.FileNames);
    }
    private async Task OpenFilesAsync(IEnumerable<string> paths)
    {
        foreach (var original in paths)
        {
            if (closing) return;
            string? directory = null;
            string? path = null;
            try
            {
                path = Path.GetFullPath(original);
                var existing = Entries.FirstOrDefault(entry => string.Equals(entry.Input, path, StringComparison.OrdinalIgnoreCase));
                if (existing != null) { documents.SelectedItem = existing; continue; }
                if (!pending.Add(path)) continue;
                UpdateActions(); status.Text = "Apertura di " + Path.GetFileName(path) + "…";
                directory = Path.Combine(sessionDirectory, Guid.NewGuid().ToString("N"));
                var result = await bridge.OpenAsync(path, directory);
                if (closing) return;
                documents.BeginUpdate();
                foreach (var document in result.Documents) documents.Items.Add(new OpenDocument(document, path));
                documents.EndUpdate();
                if (result.Documents.Count > 0) documents.SelectedIndex = documents.Items.Count - result.Documents.Count;
                if (result.Warnings.Count > 0) ShowNotice(string.Join("\n", result.Warnings));
            }
            catch (Exception error)
            {
                if (directory != null) TryDeleteDirectory(directory);
                if (!closing) ShowError("Impossibile aprire il documento", error);
            }
            finally { if (path != null) pending.Remove(path); if (!closing) UpdateActions(); }
        }
    }
    internal async void OpenFromShell(IEnumerable<string> paths)
    {
        if (closing) return;
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Show(); Activate();
        await OpenFilesAsync(paths);
    }

    private async Task SelectDocumentAsync()
    {
        UpdateActions(); UpdateInspector();
        var entry = Active;
        if (previewId == entry?.Document.Id) return;
        previewId = entry?.Document.Id;
        loadedPreviewId = null; UpdateActions();
        Text = entry == null ? "Lettore Fatture" : entry.Document.Title + " — Lettore Fatture";
        try
        {
            await pdf.OpenAsync(entry?.Document.PdfPath);
            if (entry?.Document.Id == previewId && !closing) { loadedPreviewId = previewId; UpdateActions(); }
        }
        catch (Exception error) { if (!closing) ShowNotice("PDF non leggibile: " + error.Message); }
    }

    private void UpdateActions()
    {
        save.Enabled = Active != null;
        print.Enabled = Active != null && Active.Document.Id == loadedPreviewId && pdf.PageCount > 0 && !printing;
        excel.Enabled = !exporting && pending.Count == 0 && Entries.Any(entry => entry.Document.Invoice != null);
        closeSelection.Visible = documents.SelectedItems.Count > 1;
        closeSelection.Text = $"Chiudi {documents.SelectedItems.Count} selezionati";
        progress.Visible = exporting || printing || pending.Count > 0;
        status.Text = pending.Count > 0 ? $"Apertura di {pending.Count} file…" : $"{documents.Items.Count} documenti";
    }
    private void UpdateInspector()
    {
        inspector.SuspendLayout();
        foreach (Control child in inspector.Controls.Cast<Control>().ToArray()) child.Dispose();
        var entry = Active;
        if (entry == null) { AddInfo("Informazioni", "Seleziona un documento."); }
        else
        {
            AddInfo(entry.Document.Kind == "invoice" ? "Fattura" : entry.Document.Kind == "email" ? "Email" : "PDF", entry.Document.Title);
            var button = new Button { Text = "Salva PDF…", AutoSize = true, FlatStyle = FlatStyle.System, Margin = new(0, 0, 0, 18) };
            button.Click += (_, _) => SavePdf(); inspector.Controls.Add(button);
            foreach (var item in entry.Document.Metadata.Where(item => item.Value.Length > 0)) AddInfo(item.Label, item.Value);
            AddInfo("Firme", entry.Document.Signatures.Count == 0 ? "Nessuna firma verificata sul documento." : "");
            foreach (var signature in entry.Document.Signatures)
            {
                AddInfo(signature.Subject, signature.Integrity, signature.Integrity == "Integrità verificata" ? Color.DarkGreen : Color.DarkOrange);
                AddInfo("Formato", signature.Format);
                if (signature.SignedAt != null) AddInfo("Data dichiarata", HumanDate(signature.SignedAt));
                AddInfo("Certificato", HumanDate(signature.ValidFrom) + " – " + HumanDate(signature.ValidUntil));
                AddInfo("Emittente", signature.Issuer);
            }
            if (entry.Document.Signatures.Count > 0)
                AddInfo("Controllo locale", "Catena di fiducia, revoca e marche temporali non verificate. La data dichiarata non è una marca temporale.");
            AddInfo("File sorgente", entry.Input);
        }
        ResizeInspector(); inspector.ResumeLayout();
    }
    private void AddInfo(string title, string value, Color? color = null)
    {
        var panel = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new(0, 0, 0, 18) };
        panel.Controls.Add(new Label { Text = title, AutoSize = true, ForeColor = Color.DimGray, Margin = new(0, 0, 0, 5) });
        if (value.Length > 0) panel.Controls.Add(new TextBox { Text = value, ReadOnly = true, Multiline = true,
            BorderStyle = BorderStyle.None, BackColor = Color.White, ForeColor = color ?? Color.FromArgb(35, 40, 47),
            TabStop = true, Margin = Padding.Empty });
        inspector.Controls.Add(panel);
    }
    private void ResizeInspector()
    {
        var width = Math.Max(100, inspector.ClientSize.Width - inspector.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 4);
        foreach (var panel in inspector.Controls.OfType<FlowLayoutPanel>())
        {
            panel.MaximumSize = new(width, 0);
            foreach (var text in panel.Controls.OfType<TextBox>())
            {
                text.Width = width;
                text.Height = TextRenderer.MeasureText(text.Text, text.Font, new(width, int.MaxValue), TextFormatFlags.WordBreak).Height + 8;
            }
        }
    }
    private static string HumanDate(string value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
        DateTimeStyles.None, out var date) ? date.ToLocalTime().ToString("g", CultureInfo.GetCultureInfo("it-IT")) : value;

    private void SavePdf()
    {
        if (Active is not { } entry) return;
        using var panel = new SaveFileDialog { Filter = "Documento PDF|*.pdf", DefaultExt = "pdf", AddExtension = true,
            FileName = entry.Document.SuggestedFileName, Title = "Salva PDF" };
        if (panel.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var temporary = Path.Combine(Path.GetDirectoryName(panel.FileName)!, ".fatture-pdf-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.Copy(entry.Document.PdfPath, temporary);
                File.Move(temporary, panel.FileName, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        catch (Exception error) { ShowError("Impossibile salvare il PDF", error); }
    }
    private async void ExportExcel()
    {
        if (!excel.Enabled) return;
        using var panel = new SaveFileDialog { Filter = "Cartella di lavoro Excel|*.xlsx", DefaultExt = "xlsx", AddExtension = true,
            FileName = $"Fatture-{DateTime.Now:yyyy-MM-dd}.xlsx", Title = "Esporta tutte le fatture aperte" };
        if (panel.ShowDialog(this) != DialogResult.OK) return;
        exporting = true; UpdateActions();
        try
        {
            var result = await bridge.ExportAsync(Entries.Select(entry => entry.Document).ToArray(), panel.FileName);
            if (!closing) ShowNotice($"Esportate {result.InvoiceCount} fatture. Copie identiche escluse: {result.DuplicateCount}.");
        }
        catch (Exception error) { if (!closing) ShowError("Impossibile esportare le fatture", error); }
        finally { exporting = false; if (!closing) UpdateActions(); }
    }
    private void OpenExternal()
    {
        if (Active is not { } entry) return;
        try { Process.Start(new ProcessStartInfo(entry.Document.PdfPath) { UseShellExecute = true }); }
        catch (Exception error) { ShowError("Impossibile aprire il PDF", error); }
    }
    private async void PrintPdf()
    {
        if (Active is not { } entry || !print.Enabled) return;
        using var job = new PrintDocument { DocumentName = entry.Document.Title };
        job.PrinterSettings.MinimumPage = 1; job.PrinterSettings.MaximumPage = pdf.PageCount;
        job.PrinterSettings.FromPage = 1; job.PrinterSettings.ToPage = pdf.PageCount;
        using var dialog = new PrintDialog { Document = job, AllowSomePages = true, UseEXDialog = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        printing = true; UpdateActions();
        try
        {
            // A snapshot lets users close the document while the spooler works.
            await PdfPrinting.PrintAsync(File.ReadAllBytes(entry.Document.PdfPath), job, lifetime.Token);
            if (!closing) ShowNotice("Documento inviato alla stampante.");
        }
        catch (Exception error) { if (!closing) ShowError("Impossibile stampare il PDF", error); }
        finally
        {
            printing = false; if (!closing) UpdateActions();
        }
    }
    private void RegisterFiles()
    {
        try
        {
            FileAssociations.Register();
            ShowNotice("Registrata in Apri con. Scegli le estensioni nelle impostazioni di Windows.");
            FileAssociations.ShowSettings();
        }
        catch (Exception error) { ShowError("Impossibile registrare le estensioni", error); }
    }
    private void CloseSelected() => Remove(documents.SelectedItems.Cast<OpenDocument>().ToArray());
    private void Remove(OpenDocument[] removed)
    {
        if (removed.Length == 0) return;
        var retained = documents.SelectedItems.Cast<OpenDocument>().Except(removed).ToArray();
        var index = documents.Items.IndexOf(removed[0]);
        documents.BeginUpdate();
        foreach (var entry in removed) documents.Items.Remove(entry);
        documents.ClearSelected();
        foreach (var entry in retained) documents.SelectedItems.Add(entry);
        if (documents.SelectedItems.Count == 0 && documents.Items.Count > 0)
            documents.SelectedIndex = Math.Clamp(index, 0, documents.Items.Count - 1);
        documents.EndUpdate();
        previewTask = SelectDocumentAsync();
        _ = RemovePdfsAsync(removed, previewTask);
    }
    private static async Task RemovePdfsAsync(OpenDocument[] removed, Task preview)
    {
        await preview;
        foreach (var entry in removed)
            try { File.Delete(entry.Document.PdfPath); } catch (IOException) { }
    }
    private void ShowNotice(string value)
    {
        if (closing) return;
        notice.Text = value.Replace('\n', ' '); tips.SetToolTip(notice, value); notice.Visible = true;
    }
    private void ShowError(string title, Exception error)
    {
        if (arguments.Contains("--smoke-test")) throw new InvalidOperationException(title, error);
        MessageBox.Show(this, error.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        if (shutdownComplete) { base.OnFormClosing(e); return; }
        e.Cancel = true;
        base.OnFormClosing(e);
        if (closing) return;
        closing = true; Enabled = false;
        lifetime.Cancel(); bridge.Dispose();
        // Let any native PDF operation release its file before deleting the
        // session. Keeping the message loop alive here avoids an STA deadlock.
        try { await pdf.CloseAsync(); }
        finally
        {
            pdf.Dispose(); tips.Dispose(); TryDeleteDirectory(sessionDirectory);
            shutdownComplete = true;
            BeginInvoke((Action)Close);
        }
    }
    private static void TryDeleteDirectory(string directory)
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
