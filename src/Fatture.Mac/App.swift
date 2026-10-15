import AppKit
import PDFKit
import UniformTypeIdentifiers
import Darwin

@main
final class App: NSObject, NSApplicationDelegate, NSTableViewDataSource, NSTableViewDelegate, NSToolbarDelegate, NSToolbarItemValidation, NSMenuItemValidation {
    private var window: NSWindow!
    private let split = NSSplitViewController()
    private let table = DocumentTable()
    private let pdf = ReaderPDFView()
    private var pdfTop: NSLayoutConstraint!
    private let empty = NSStackView()
    private let inspector = NSStackView()
    private var inspectorItem: NSSplitViewItem!
    private let countLabel = NSTextField(labelWithString: "Nessun documento")
    private let closeSelectionButton = NSButton(title: "Chiudi selezionati", target: nil, action: nil)
    private let pageLabel = NSTextField(labelWithString: "")
    private let activity = NSProgressIndicator()
    private let notice = NSTextField(wrappingLabelWithString: "")
    private let saveButton = NSButton(title: "Salva PDF…", target: nil, action: nil)
    private let bridge = Bridge()
    private var documents: [Document] = []
    private var origins: [String: URL] = [:]
    private var pending = Set<URL>()
    private var opened: [URL: [String]] = [:]
    private var messages: [String] = []
    private var ready = false
    private var earlyURLs: [URL] = []
    private var exporting = false
    private var canExportExcel: Bool { !exporting && pending.isEmpty && documents.contains { $0.kind == "invoice" && $0.invoice != nil } }
    private var activeDocument: Document? { table.selectedRow >= 0 && table.selectedRow < documents.count ? documents[table.selectedRow] : nil }
    private let sessionDirectory = FileManager.default.temporaryDirectory.appendingPathComponent("LettoreFatture-" + UUID().uuidString, isDirectory: true)

    static func main() {
        let app = NSApplication.shared; let delegate = App()
        app.delegate = delegate; app.setActivationPolicy(.regular)
        withExtendedLifetime(delegate) { app.run() }
    }
    func applicationDidFinishLaunching(_ notification: Notification) {
        try? FileManager.default.createDirectory(at: sessionDirectory, withIntermediateDirectories: true,
            attributes: [.posixPermissions: 0o700])
        buildMenu(); buildWindow(); ready = true
        let urls = earlyURLs; earlyURLs.removeAll(); openURLs(urls)
        // Direct invocation also supports file paths, useful during development.
        openURLs(CommandLine.arguments.dropFirst().filter { !$0.hasPrefix("-") && FileManager.default.fileExists(atPath: $0) }.map { URL(fileURLWithPath: $0) })
        NotificationCenter.default.addObserver(self, selector: #selector(pageChanged), name: .PDFViewPageChanged, object: pdf)
        // Opt-in integration check, run in a real AppKit window using --smoke-test.
        if CommandLine.arguments.contains("--smoke-test") { DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) { self.smokeTest() } }
    }
    func application(_ sender: NSApplication, openFiles filenames: [String]) {
        let urls = filenames.map { URL(fileURLWithPath: $0) }
        if ready { openURLs(urls) } else { earlyURLs.append(contentsOf: urls) }
        sender.reply(toOpenOrPrint: .success)
    }
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        window.makeKeyAndOrderFront(nil); return true
    }
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }
    func applicationWillTerminate(_ notification: Notification) {
        bridge.stop(); try? FileManager.default.removeItem(at: sessionDirectory)
    }

    private func buildWindow() {
        let sidebarRoot = DropView(); sidebarRoot.onDrop = { [weak self] in self?.openURLs($0) }
        let sidebarHeader = NSTextField(labelWithString: "Documenti"); sidebarHeader.font = .systemFont(ofSize: 13, weight: .semibold)
        let plus = iconButton("plus", "Apri documenti", #selector(openPanel))
        let header = NSStackView(views: [sidebarHeader, NSView(), plus]); header.spacing = 8
        let scroll = NSScrollView(); scroll.hasVerticalScroller = true; scroll.drawsBackground = false
        table.addTableColumn(NSTableColumn(identifier: .init("document")))
        table.headerView = nil; table.rowHeight = 57; table.intercellSpacing = NSSize(width: 0, height: 3)
        table.style = .sourceList; table.backgroundColor = .clear; table.dataSource = self; table.delegate = self
        table.allowsMultipleSelection = true; table.allowsEmptySelection = true
        table.columnAutoresizingStyle = .uniformColumnAutoresizingStyle
        table.onRemove = { [weak self] in self?.removeDocument(nil) }
        let contextual = NSMenu(); contextual.addItem(withTitle: "Chiudi documento", action: #selector(removeDocument(_:)), keyEquivalent: "")
        table.menu = contextual; scroll.documentView = table
        countLabel.font = .systemFont(ofSize: 11); countLabel.textColor = .secondaryLabelColor
        activity.style = .spinning; activity.controlSize = .small; activity.isDisplayedWhenStopped = false
        closeSelectionButton.target = self; closeSelectionButton.action = #selector(removeDocument(_:))
        closeSelectionButton.bezelStyle = .rounded; closeSelectionButton.controlSize = .small; closeSelectionButton.isHidden = true
        closeSelectionButton.toolTip = "Chiudi tutti i documenti selezionati (⌘-clic o ⇧-clic)"
        let footer = NSStackView(views: [countLabel, NSView(), closeSelectionButton, activity]); footer.spacing = 8
        [header, scroll, footer].forEach { sidebarRoot.addSubview($0); $0.translatesAutoresizingMaskIntoConstraints = false }
        NSLayoutConstraint.activate([
            header.leadingAnchor.constraint(equalTo: sidebarRoot.leadingAnchor, constant: 16), header.trailingAnchor.constraint(equalTo: sidebarRoot.trailingAnchor, constant: -12),
            header.topAnchor.constraint(equalTo: sidebarRoot.topAnchor, constant: 14), header.heightAnchor.constraint(equalToConstant: 30),
            scroll.leadingAnchor.constraint(equalTo: sidebarRoot.leadingAnchor), scroll.trailingAnchor.constraint(equalTo: sidebarRoot.trailingAnchor),
            scroll.topAnchor.constraint(equalTo: header.bottomAnchor, constant: 8), scroll.bottomAnchor.constraint(equalTo: footer.topAnchor, constant: -8),
            footer.leadingAnchor.constraint(equalTo: sidebarRoot.leadingAnchor, constant: 16), footer.trailingAnchor.constraint(equalTo: sidebarRoot.trailingAnchor, constant: -12),
            footer.bottomAnchor.constraint(equalTo: sidebarRoot.bottomAnchor, constant: -12), footer.heightAnchor.constraint(equalToConstant: 22)
        ])
        let sideController = NSViewController(); sideController.view = sidebarRoot
        let sideItem = NSSplitViewItem(sidebarWithViewController: sideController); sideItem.minimumThickness = 220; sideItem.maximumThickness = 350
        split.addSplitViewItem(sideItem)

        let center = DropView(); center.onDrop = { [weak self] in self?.openURLs($0) }
        pdf.registerForDraggedTypes([.fileURL]); pdf.onDrop = { [weak self] in self?.openURLs($0) }
        pdf.autoScales = true; pdf.displayMode = .singlePageContinuous; pdf.displayDirection = .vertical
        pdf.backgroundColor = .underPageBackgroundColor; pdf.displaysPageBreaks = true
        pdf.minScaleFactor = 0.15; pdf.maxScaleFactor = 5
        let previous = iconButton("chevron.up", "Pagina precedente", #selector(previousPage))
        let next = iconButton("chevron.down", "Pagina successiva", #selector(nextPage))
        let zoomOut = iconButton("minus.magnifyingglass", "Riduci", #selector(zoomOut))
        let zoomIn = iconButton("plus.magnifyingglass", "Ingrandisci", #selector(zoomIn))
        let fit = NSButton(title: "Adatta", target: self, action: #selector(fitPage)); fit.bezelStyle = .inline
        pageLabel.font = .monospacedDigitSystemFont(ofSize: 11, weight: .regular); pageLabel.textColor = .secondaryLabelColor
        let controls = NSStackView(views: [previous, next, pageLabel, NSView(), zoomOut, zoomIn, fit]); controls.spacing = 12
        notice.font = .systemFont(ofSize: 11); notice.textColor = .secondaryLabelColor; notice.maximumNumberOfLines = 2; notice.isHidden = true
        [notice, pdf, controls, empty].forEach { center.addSubview($0); $0.translatesAutoresizingMaskIntoConstraints = false }
        pdfTop = pdf.topAnchor.constraint(equalTo: center.topAnchor)
        NSLayoutConstraint.activate([
            notice.leadingAnchor.constraint(equalTo: center.leadingAnchor, constant: 16), notice.trailingAnchor.constraint(equalTo: center.trailingAnchor, constant: -16),
            notice.topAnchor.constraint(equalTo: center.topAnchor, constant: 8),
            pdf.leadingAnchor.constraint(equalTo: center.leadingAnchor), pdf.trailingAnchor.constraint(equalTo: center.trailingAnchor),
            pdfTop, pdf.bottomAnchor.constraint(equalTo: controls.topAnchor, constant: -8),
            controls.leadingAnchor.constraint(equalTo: center.leadingAnchor, constant: 16), controls.trailingAnchor.constraint(equalTo: center.trailingAnchor, constant: -16),
            controls.heightAnchor.constraint(equalToConstant: 28), controls.bottomAnchor.constraint(equalTo: center.bottomAnchor, constant: -8),
            empty.centerXAnchor.constraint(equalTo: center.centerXAnchor), empty.centerYAnchor.constraint(equalTo: center.centerYAnchor),
            empty.widthAnchor.constraint(lessThanOrEqualTo: center.widthAnchor, constant: -48)
        ])
        empty.orientation = .vertical; empty.spacing = 16; empty.alignment = .centerX
        let icon = NSImageView(image: NSImage(systemSymbolName: "doc.text.viewfinder", accessibilityDescription: nil)!)
        icon.contentTintColor = .tertiaryLabelColor; icon.symbolConfiguration = .init(pointSize: 48, weight: .light)
        let title = NSTextField(labelWithString: "Le tue fatture, subito leggibili"); title.font = .systemFont(ofSize: 21, weight: .semibold)
        let hint = NSTextField(wrappingLabelWithString: "Trascina qui XML, P7M o EML.\nPuoi aprire più documenti insieme.")
        hint.alignment = .center; hint.textColor = .secondaryLabelColor
        let open = NSButton(title: "Apri documenti…", target: self, action: #selector(openPanel)); open.bezelStyle = .rounded
        [icon, title, hint, open].forEach { empty.addArrangedSubview($0) }
        let local = NSTextField(labelWithString: "Tutto resta sul tuo Mac"); local.font = .systemFont(ofSize: 11); local.textColor = .tertiaryLabelColor
        empty.addArrangedSubview(local); pdf.isHidden = true
        let centerController = NSViewController(); centerController.view = center
        let centerItem = NSSplitViewItem(viewController: centerController); centerItem.minimumThickness = 400; centerItem.holdingPriority = .init(200)
        split.addSplitViewItem(centerItem)

        let infoController = NSViewController(); let infoScroll = NSScrollView()
        infoScroll.hasVerticalScroller = true; infoScroll.drawsBackground = false
        inspector.orientation = .vertical; inspector.alignment = .leading; inspector.spacing = 16
        infoScroll.documentView = inspector; inspector.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            inspector.leadingAnchor.constraint(equalTo: infoScroll.contentView.leadingAnchor, constant: 20),
            inspector.trailingAnchor.constraint(equalTo: infoScroll.contentView.trailingAnchor, constant: -20),
            inspector.topAnchor.constraint(equalTo: infoScroll.contentView.topAnchor, constant: 20)
        ])
        infoController.view = infoScroll
        inspectorItem = NSSplitViewItem(viewController: infoController); inspectorItem.minimumThickness = 245; inspectorItem.maximumThickness = 320
        inspectorItem.canCollapse = true; inspectorItem.isCollapsed = true
        split.addSplitViewItem(inspectorItem)
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1280, height: 850), styleMask: [.titled, .closable, .miniaturizable, .resizable], backing: .buffered, defer: false)
        window.title = "Lettore Fatture"; window.subtitle = "XML · P7M · EML"
        window.minSize = NSSize(width: 920, height: 580); window.contentViewController = split
        window.setFrameAutosaveName("LettoreFatture.main"); window.isReleasedWhenClosed = false
        let toolbar = NSToolbar(identifier: "main"); toolbar.delegate = self; toolbar.displayMode = .iconOnly
        window.toolbar = toolbar; window.toolbarStyle = .unified; window.center()
        window.setContentSize(NSSize(width: 1280, height: 850)); window.center()
        saveButton.target = self; saveButton.action = #selector(savePdf); saveButton.bezelStyle = .rounded
        saveButton.controlSize = .large; saveButton.image = NSImage(systemSymbolName: "square.and.arrow.down", accessibilityDescription: nil)
        updateInspector(); window.makeKeyAndOrderFront(nil); NSApp.activate(ignoringOtherApps: true)
        DispatchQueue.main.async {
            self.split.splitView.setPosition(250, ofDividerAt: 0)
        }
    }

    private func iconButton(_ symbol: String, _ title: String, _ action: Selector) -> NSButton {
        let button = NSButton(image: NSImage(systemSymbolName: symbol, accessibilityDescription: title)!, target: self, action: action)
        button.bezelStyle = .texturedRounded; button.isBordered = false; button.toolTip = title; button.setAccessibilityLabel(title); return button
    }
    func toolbarAllowedItemIdentifiers(_ toolbar: NSToolbar) -> [NSToolbarItem.Identifier] { [.init("open"), .flexibleSpace, .init("excel"), .init("save"), .init("print"), .init("info")] }
    func toolbarDefaultItemIdentifiers(_ toolbar: NSToolbar) -> [NSToolbarItem.Identifier] { toolbarAllowedItemIdentifiers(toolbar) }
    func toolbar(_ toolbar: NSToolbar, itemForItemIdentifier id: NSToolbarItem.Identifier, willBeInsertedIntoToolbar flag: Bool) -> NSToolbarItem? {
        let item = NSToolbarItem(itemIdentifier: id); item.target = self
        switch id.rawValue {
        case "open": item.label = "Apri"; item.image = NSImage(systemSymbolName: "folder.badge.plus", accessibilityDescription: "Apri documenti"); item.action = #selector(openPanel)
        case "save": item.label = "Salva PDF"; item.image = NSImage(systemSymbolName: "square.and.arrow.down", accessibilityDescription: "Salva PDF"); item.action = #selector(savePdf)
        case "excel": item.label = "Esporta fatture in Excel…"; item.toolTip = "Esporta tutte le fatture aperte in un unico Excel"; item.image = NSImage(systemSymbolName: "tablecells", accessibilityDescription: item.label); item.action = #selector(exportExcel)
        case "print": item.label = "Stampa"; item.image = NSImage(systemSymbolName: "printer", accessibilityDescription: "Stampa"); item.action = #selector(printPdf)
        case "info": item.label = "Informazioni"; item.image = NSImage(systemSymbolName: "sidebar.right", accessibilityDescription: "Mostra informazioni"); item.action = #selector(toggleInfo)
        default: return nil
        }
        return item
    }
    func validateToolbarItem(_ item: NSToolbarItem) -> Bool {
        if item.action == #selector(exportExcel) { return canExportExcel }
        if [#selector(savePdf), #selector(printPdf)].contains(item.action) { return activeDocument != nil }
        return true
    }

    private func buildMenu() {
        let main = NSMenu()
        func menu(_ name: String) -> NSMenu {
            let item = NSMenuItem(); let sub = NSMenu(title: name); item.submenu = sub; main.addItem(item); return sub
        }
        func add(_ menu: NSMenu, _ title: String, _ action: Selector, _ key: String, _ modifiers: NSEvent.ModifierFlags = [.command]) {
            let item = menu.addItem(withTitle: title, action: action, keyEquivalent: key); item.keyEquivalentModifierMask = modifiers
        }
        let app = menu("Lettore Fatture")
        add(app, "Informazioni su Lettore Fatture", #selector(NSApplication.orderFrontStandardAboutPanel(_:)), "")
        app.addItem(.separator()); add(app, "Nascondi Lettore Fatture", #selector(NSApplication.hide(_:)), "h")
        app.addItem(.separator()); add(app, "Esci da Lettore Fatture", #selector(NSApplication.terminate(_:)), "q")
        let file = menu("File")
        add(file, "Apri…", #selector(openPanel), "o"); add(file, "Salva PDF…", #selector(savePdf), "s")
        add(file, "Esporta fatture in Excel…", #selector(exportExcel), "e", [.command, .shift])
        add(file, "Stampa…", #selector(printPdf), "p"); file.addItem(.separator())
        add(file, "Chiudi documento", #selector(removeDocument(_:)), "w")
        add(file, "Chiudi tutti i documenti", #selector(removeAll), "w", [.command, .option])
        file.addItem(.separator()); add(file, "Associa estensioni…", #selector(associateExtensions), "")
        let edit = menu("Modifica")
        add(edit, "Copia", #selector(NSText.copy(_:)), "c"); add(edit, "Seleziona tutto", #selector(NSText.selectAll(_:)), "a")
        let view = menu("Vista")
        add(view, "Mostra / nascondi informazioni", #selector(toggleInfo), "i", [.command, .option])
        add(view, "Ingrandisci", #selector(zoomIn), "+"); add(view, "Riduci", #selector(zoomOut), "-"); add(view, "Adatta pagina", #selector(fitPage), "0")
        let win = menu("Finestra"); add(win, "Contrai", #selector(NSWindow.performMiniaturize(_:)), "m")
        add(win, "Zoom", #selector(NSWindow.performZoom(_:)), ""); NSApp.windowsMenu = win
        NSApp.mainMenu = main
    }

    @objc private func openPanel() {
        let panel = NSOpenPanel(); panel.allowsMultipleSelection = true; panel.canChooseDirectories = false
        panel.allowedContentTypes = ["xml", "p7m", "eml", "pdf"].compactMap { UTType(filenameExtension: $0) }
        panel.message = "Apri fatture XML, documenti firmati P7M o email EML."
        panel.beginSheetModal(for: window) { if $0 == .OK { self.openURLs(panel.urls) } }
    }
    private func openURLs(_ urls: [URL]) {
        for original in urls {
            let url = original.resolvingSymlinksInPath().standardizedFileURL
            if let ids = opened[url], let index = documents.firstIndex(where: { ids.contains($0.id) }) {
                table.selectRowIndexes(IndexSet(integer: index), byExtendingSelection: false); continue
            }
            guard !pending.contains(url) else { continue }
            pending.insert(url); activity.startAnimation(nil)
            window.toolbar?.validateVisibleItems()
            countLabel.stringValue = "Apertura di \(pending.count) file…"
            let directory = sessionDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
            bridge.open(url, outputDirectory: directory) { result in
                self.pending.remove(url)
                switch result {
                case .success(let value):
                    let index = self.documents.count
                    self.documents.append(contentsOf: value.documents); self.opened[url] = value.documents.map(\.id)
                    value.documents.forEach { self.origins[$0.id] = url }
                    self.messages.append(contentsOf: value.warnings)
                    if value.documents.isEmpty { self.messages.append("\(url.lastPathComponent): nessun documento visualizzabile.") }
                    self.table.reloadData()
                    if index < self.documents.count { self.table.selectRowIndexes(IndexSet(integer: index), byExtendingSelection: false) }
                case .failure(let error):
                    try? FileManager.default.removeItem(at: directory)
                    self.messages.append("\(url.lastPathComponent): \(error.localizedDescription)")
                    self.showError("Impossibile aprire \(url.lastPathComponent)", error.localizedDescription)
                }
                if self.pending.isEmpty && !self.exporting { self.activity.stopAnimation(nil) }
                self.window.toolbar?.validateVisibleItems()
                self.countLabel.stringValue = self.pending.isEmpty ? "\(self.documents.count) documenti" : "Apertura di \(self.pending.count) file…"
                self.notice.stringValue = self.messages.last ?? ""; self.notice.toolTip = self.messages.joined(separator: "\n\n")
                self.notice.isHidden = self.messages.isEmpty
                self.pdfTop.constant = self.messages.isEmpty ? 0 : 44
            }
        }
        window.makeKeyAndOrderFront(nil)
    }
    func numberOfRows(in tableView: NSTableView) -> Int { documents.count }
    func tableView(_ tableView: NSTableView, viewFor tableColumn: NSTableColumn?, row: Int) -> NSView? {
        let id = NSUserInterfaceItemIdentifier("cell")
        let cell = tableView.makeView(withIdentifier: id, owner: self) as? DocumentCell ?? DocumentCell()
        cell.identifier = id; let doc = documents[row]
        cell.title.stringValue = doc.title; cell.detail.stringValue = doc.subtitle
        cell.symbol.image = NSImage(systemSymbolName: doc.symbol, accessibilityDescription: doc.kindLabel)
        cell.onClose = { [weak self] in self?.closeDocument(id: doc.id) }
        cell.toolTip = "\(doc.title)\n\(doc.sourceName)"; return cell
    }
    func tableViewSelectionDidChange(_ notification: Notification) { selectDocument() }
    private func selectDocument() {
        if let doc = activeDocument {
            let loaded = PDFDocument(url: URL(fileURLWithPath: doc.pdfPath))
            if loaded == nil { showError("PDF non leggibile", "Il PDF allegato potrebbe essere danneggiato o cifrato.") }
            pdf.document = loaded; pdf.autoScales = true; pdf.isHidden = loaded == nil; empty.isHidden = loaded != nil
            window.subtitle = doc.title
        } else {
            pdf.document = nil; pdf.isHidden = true; empty.isHidden = false; window.subtitle = "XML · P7M · EML"
        }
        closeSelectionButton.isHidden = table.selectedRowIndexes.count < 2
        pageChanged(); updateInspector(); window.toolbar?.validateVisibleItems()
    }
    @objc private func pageChanged() {
        if let doc = pdf.document, let page = pdf.currentPage { pageLabel.stringValue = "\(doc.index(for: page) + 1) / \(doc.pageCount)" }
        else { pageLabel.stringValue = "" }
    }
    private func updateInspector() {
        for view in inspector.arrangedSubviews { inspector.removeArrangedSubview(view); view.removeFromSuperview() }
        func label(_ value: String, size: CGFloat = 12, color: NSColor = .labelColor, bold: Bool = false) {
            let text = NSTextField(wrappingLabelWithString: value); text.font = .systemFont(ofSize: size, weight: bold ? .semibold : .regular)
            text.textColor = color; text.isSelectable = true
            inspector.addArrangedSubview(text); text.widthAnchor.constraint(equalTo: inspector.widthAnchor).isActive = true
        }
        guard let doc = activeDocument else {
            label("Informazioni", size: 15, bold: true); label("Seleziona un documento per vederne i dettagli.", color: .secondaryLabelColor)
            return
        }
        label(doc.kindLabel.uppercased(), size: 10, color: .secondaryLabelColor, bold: true)
        label(doc.title, size: 17, bold: true)
        inspector.addArrangedSubview(saveButton); saveButton.widthAnchor.constraint(equalTo: inspector.widthAnchor).isActive = true
        label("Documento", size: 13, bold: true)
        for item in doc.metadata where !item.value.isEmpty {
            let stack = NSStackView(); stack.orientation = .vertical; stack.alignment = .leading; stack.spacing = 4
            let title = NSTextField(labelWithString: item.label); title.font = .systemFont(ofSize: 10); title.textColor = .secondaryLabelColor
            let value = NSTextField(wrappingLabelWithString: item.value); value.font = .systemFont(ofSize: 12); value.isSelectable = true
            stack.addArrangedSubview(title); stack.addArrangedSubview(value); inspector.addArrangedSubview(stack)
            stack.widthAnchor.constraint(equalTo: inspector.widthAnchor).isActive = true
            value.widthAnchor.constraint(equalTo: stack.widthAnchor).isActive = true
        }
        label("Firme", size: 13, bold: true)
        if doc.signatures.isEmpty { label("Nessuna firma verificata sul documento.", color: .secondaryLabelColor) }
        for signature in doc.signatures {
            label(signature.subject, bold: true)
            label(signature.integrity, color: signature.integrity == "Integrità verificata" ? .systemGreen : .systemOrange)
            label(signature.format, size: 10, color: .secondaryLabelColor)
            if let date = signature.signedAt { label("Data dichiarata: \(humanDate(date))", size: 11) }
            label("Certificato: \(humanDate(signature.validFrom)) – \(humanDate(signature.validUntil))", size: 10, color: .secondaryLabelColor)
            label("Emittente: \(signature.issuer)", size: 10, color: .secondaryLabelColor)
        }
        if !doc.signatures.isEmpty {
            label("Controllo crittografico locale. Catena di fiducia, revoca e marche temporali non verificate. La data dichiarata non è una marca temporale.", size: 10, color: .secondaryLabelColor)
        }
        label("File sorgente", size: 13, bold: true); label(doc.sourceName, size: 10, color: .secondaryLabelColor)
        if let source = origins[doc.id] { label(source.path, size: 10, color: .secondaryLabelColor) }
        // Give the document view its intrinsic height, allowing the inspector to scroll.
        inspector.layoutSubtreeIfNeeded()
        inspector.setFrameSize(NSSize(width: inspector.frame.width, height: inspector.fittingSize.height + 40))
    }
    private func humanDate(_ value: String) -> String {
        let parser = ISO8601DateFormatter(); parser.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        let date = parser.date(from: value) ?? ISO8601DateFormatter().date(from: value)
        guard let date else { return value }
        let formatter = DateFormatter(); formatter.locale = Locale(identifier: "it_IT"); formatter.dateStyle = .short; formatter.timeStyle = .short
        return formatter.string(from: date)
    }
    @objc private func savePdf() {
        guard let doc = activeDocument else { return }
        let panel = NSSavePanel(); panel.allowedContentTypes = [.pdf]; panel.nameFieldStringValue = doc.suggestedFileName
        panel.beginSheetModal(for: window) { response in
            guard response == .OK, let target = panel.url else { return }
            do {
                let data = try Data(contentsOf: URL(fileURLWithPath: doc.pdfPath), options: .mappedIfSafe)
                try data.write(to: target, options: .atomic)
            } catch { self.showError("Impossibile salvare il PDF", error.localizedDescription) }
        }
    }
    @objc private func printPdf() {
        guard let operation = pdf.document?.printOperation(for: NSPrintInfo.shared, scalingMode: .pageScaleToFit, autoRotate: true) else { return }
        operation.runModal(for: window, delegate: nil, didRun: nil, contextInfo: nil)
    }
    @objc private func exportExcel() {
        guard canExportExcel else { return }
        let panel = NSSavePanel()
        panel.allowedContentTypes = [UTType(filenameExtension: "xlsx") ?? .data]
        let formatter = DateFormatter(); formatter.dateFormat = "yyyy-MM-dd"
        panel.nameFieldStringValue = "Fatture-\(formatter.string(from: Date())).xlsx"
        panel.prompt = "Esporta"
        panel.message = "Esporta tutte le fatture aperte. Email e documenti che non sono fatture vengono ignorati."
        panel.beginSheetModal(for: window) { response in
            guard response == .OK, let destination = panel.url else { return }
            self.exporting = true; self.activity.startAnimation(nil); self.window.toolbar?.validateVisibleItems()
            self.bridge.exportExcel(self.documents, to: destination) { result in
                self.exporting = false
                if self.pending.isEmpty { self.activity.stopAnimation(nil) }
                self.window.toolbar?.validateVisibleItems()
                switch result {
                case .success(let exported):
                    var message = "Esportate \(exported.invoiceCount) fatture in \(destination.lastPathComponent)."
                    if exported.duplicateCount > 0 { message += " Copie identiche escluse: \(exported.duplicateCount)." }
                    self.notice.stringValue = message; self.notice.toolTip = destination.path
                    self.notice.isHidden = false; self.pdfTop.constant = 44
                case .failure(let error): self.showError("Impossibile esportare le fatture", error.localizedDescription)
                }
            }
        }
    }
    @objc private func associateExtensions() {
        let alert = NSAlert(); alert.messageText = "Apri questi file con Lettore Fatture"
        alert.informativeText = "Scegli le estensioni da aprire automaticamente con questa app. L’associazione XML si applica a tutti i file XML."
        let choices = [("Fatture XML (.xml)", "xml"), ("Documenti firmati (.p7m)", "p7m"), ("Email (.eml)", "eml")]
        let stack = NSStackView(); stack.orientation = .vertical; stack.alignment = .leading; stack.spacing = 10
        var buttons: [NSButton] = []
        for choice in choices {
            let button = NSButton(checkboxWithTitle: choice.0, target: nil, action: nil)
            button.state = choice.1 == "xml" ? .off : .on; stack.addArrangedSubview(button); buttons.append(button)
        }
        stack.frame = NSRect(x: 0, y: 0, width: 320, height: 90); alert.accessoryView = stack
        alert.addButton(withTitle: "Associa"); alert.addButton(withTitle: "Annulla")
        alert.beginSheetModal(for: window) { response in
            guard response == .alertFirstButtonReturn else { return }
            for (index, choice) in choices.enumerated() where buttons[index].state == .on {
                guard let type = UTType(filenameExtension: choice.1) else { continue }
                NSWorkspace.shared.setDefaultApplication(at: Bundle.main.bundleURL, toOpen: type) { error in
                    if let error { DispatchQueue.main.async { self.showError("Associazione .\(choice.1) non riuscita", error.localizedDescription) } }
                }
            }
        }
    }
    @objc private func toggleInfo() { inspectorItem.animator().isCollapsed.toggle() }
    @objc private func previousPage() { pdf.goToPreviousPage(nil) }
    @objc private func nextPage() { pdf.goToNextPage(nil) }
    @objc private func zoomIn() { pdf.zoomIn(nil) }
    @objc private func zoomOut() { pdf.zoomOut(nil) }
    @objc private func fitPage() { pdf.autoScales = true }
    @objc private func removeDocument(_ sender: Any?) {
        if let item = sender as? NSMenuItem, item.menu === table.menu, table.clickedRow >= 0 {
            closeDocument(id: documents[table.clickedRow].id)
        } else {
            removeDocuments(at: table.selectedRowIndexes)
        }
    }
    private func closeDocument(id: String) {
        guard let index = documents.firstIndex(where: { $0.id == id }) else { return }
        let selection = table.selectedRowIndexes
        removeDocuments(at: selection.contains(index) ? selection : IndexSet(integer: index))
    }
    private func removeDocuments(at indexes: IndexSet) {
        let valid = indexes.filter { documents.indices.contains($0) }
        guard let first = valid.first else { return }
        let removed = valid.map { documents[$0] }
        let removedIDs = Set(removed.map(\.id))
        let retainedSelection = Set(table.selectedRowIndexes.filter { documents.indices.contains($0) }.map { documents[$0].id }).subtracting(removedIDs)
        // Release the preview before removing its temporary PDF, then restore
        // selection by identity so closing other rows never changes the document.
        table.deselectAll(nil)
        documents.removeAll { removedIDs.contains($0.id) }
        for doc in removed {
            origins.removeValue(forKey: doc.id)
            try? FileManager.default.removeItem(atPath: doc.pdfPath)
        }
        opened = opened.mapValues { $0.filter { !removedIDs.contains($0) } }.filter { !$0.value.isEmpty }
        table.reloadData()
        let selection = IndexSet(documents.indices.filter { retainedSelection.contains(documents[$0].id) })
        if !selection.isEmpty { table.selectRowIndexes(selection, byExtendingSelection: false) }
        else if !documents.isEmpty { table.selectRowIndexes(IndexSet(integer: min(first, documents.count - 1)), byExtendingSelection: false) }
        countLabel.stringValue = "\(documents.count) documenti"; selectDocument()
    }
    @objc private func removeAll() {
        documents.forEach { try? FileManager.default.removeItem(atPath: $0.pdfPath) }
        documents.removeAll(); origins.removeAll(); opened.removeAll(); messages.removeAll()
        table.reloadData(); countLabel.stringValue = "Nessun documento"; notice.isHidden = true; pdfTop.constant = 0; selectDocument()
    }
    func validateMenuItem(_ menuItem: NSMenuItem) -> Bool {
        if menuItem.action == #selector(exportExcel) { return canExportExcel }
        if menuItem.action == #selector(removeDocument(_:)) {
            let contextualRow = menuItem.menu === table.menu && documents.indices.contains(table.clickedRow) ? table.clickedRow : nil
            let count = contextualRow.map { table.selectedRowIndexes.contains($0) ? table.selectedRowIndexes.count : 1 } ?? table.selectedRowIndexes.count
            menuItem.title = count > 1 ? "Chiudi \(count) documenti" : "Chiudi documento"
            return count > 0
        }
        if [#selector(savePdf), #selector(printPdf)].contains(menuItem.action) { return activeDocument != nil }
        return true
    }
    private func showError(_ title: String, _ message: String) {
        let alert = NSAlert(); alert.messageText = title; alert.informativeText = message; alert.alertStyle = .warning
        alert.beginSheetModal(for: window)
    }
    private func smokeTest() {
        guard let argument = ProcessInfo.processInfo.environment["FATTURE_SMOKE_INPUT"] else { fputs("Missing FATTURE_SMOKE_INPUT\n", stderr); exit(1) }
        let url = URL(fileURLWithPath: argument)
        let watchdog = DispatchWorkItem { fputs("Smoke test: timeout del bridge\n", stderr); exit(1) }
        DispatchQueue.main.asyncAfter(deadline: .now() + 30, execute: watchdog)
        bridge.open(url, outputDirectory: sessionDirectory.appendingPathComponent("smoke")) { result in
            watchdog.cancel()
            do {
                let value = try result.get()
                guard let doc = value.documents.first, let loaded = PDFDocument(url: URL(fileURLWithPath: doc.pdfPath)), loaded.pageCount > 0,
                      loaded.string?.isEmpty == false else { throw ReaderError(message: "PDFKit non legge il documento.") }
                self.documents = value.documents; self.table.reloadData()
                self.table.selectRowIndexes(IndexSet(integer: 0), byExtendingSelection: false)
                self.window.layoutIfNeeded()
                guard self.pdf.document != nil, self.pdf.bounds.width > 300, self.inspector.arrangedSubviews.count > 3 else {
                    throw ReaderError(message: "Layout principale non valido.")
                }
                self.countLabel.stringValue = "\(self.documents.count) documenti"
                if let destination = ProcessInfo.processInfo.environment["FATTURE_SMOKE_EXPORT_PATH"] {
                    guard self.canExportExcel else { throw ReaderError(message: "Export Excel disabilitato con una fattura aperta.") }
                    let email = Document(id: UUID().uuidString, kind: "email", title: "Email di prova", subtitle: "", sourceName: "prova.eml",
                        pdfPath: doc.pdfPath, suggestedFileName: "email.pdf", metadata: [], signatures: [])
                    self.bridge.exportExcel(self.documents + [email], to: URL(fileURLWithPath: destination)) { result in
                        do {
                            let exported = try result.get()
                            guard exported.invoiceCount == value.documents.filter({ $0.kind == "invoice" }).count,
                                  FileManager.default.fileExists(atPath: exported.path) else { throw ReaderError(message: "Export Excel non coerente con i documenti aperti.") }
                            print("AppKit + Excel OK: \(exported.invoiceCount) fatture; email ignorata")
                            NSApp.terminate(nil)
                        } catch { fputs("Smoke export: \(error)\n", stderr); exit(1) }
                    }
                    return
                }
                if ProcessInfo.processInfo.environment["FATTURE_SMOKE_CLOSE"] == "1" {
                    try self.smokeTestClosing(source: url)
                    print("AppKit OK: X singola, selezione multipla, X sui selezionati e chiusura dal pulsante")
                    NSApp.terminate(nil)
                    return
                }
                DispatchQueue.main.asyncAfter(deadline: .now() + 1) {
                    guard self.inspectorItem.isCollapsed else {
                        fputs("Smoke test: pannello informazioni aperto all’avvio\n", stderr); exit(1)
                    }
                    if let destination = ProcessInfo.processInfo.environment["FATTURE_SMOKE_SCREENSHOT"],
                       let bitmap = self.window.contentView?.bitmapImageRepForCachingDisplay(in: self.window.contentView!.bounds) {
                        self.window.contentView!.cacheDisplay(in: self.window.contentView!.bounds, to: bitmap)
                        try? bitmap.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: destination))
                    }
                    print("AppKit + PDFKit OK: \(value.documents.count) documenti, \(loaded.pageCount) pagine, PDF \(Int(self.pdf.bounds.width)) pt")
                    if ProcessInfo.processInfo.environment["FATTURE_SMOKE_WINDOW_CLOSE"] == "ready" {
                        self.window.performClose(nil)
                    } else {
                        NSApp.terminate(nil)
                    }
                }
            } catch { fputs("Smoke test: \(error)\n", stderr); exit(1) }
        }
        if ProcessInfo.processInfo.environment["FATTURE_SMOKE_WINDOW_CLOSE"] == "loading" {
            window.performClose(nil)
        }
    }

    private func smokeTestClosing(source: URL) throws {
        guard let template = documents.first, documents.count == 1 else { throw ReaderError(message: "Per il test chiusura serve una fattura singola.") }
        for _ in 0..<4 {
            let id = UUID().uuidString
            let path = sessionDirectory.appendingPathComponent(id + ".pdf")
            try FileManager.default.copyItem(at: URL(fileURLWithPath: template.pdfPath), to: path)
            documents.append(Document(id: id, kind: template.kind, title: template.title, subtitle: template.subtitle,
                sourceName: template.sourceName, pdfPath: path.path, suggestedFileName: template.suggestedFileName,
                metadata: template.metadata, signatures: template.signatures))
        }
        let original = documents
        opened[source] = documents.map(\.id)
        documents.forEach { origins[$0.id] = source }
        table.reloadData(); table.selectRowIndexes(IndexSet([0, 1]), byExtendingSelection: false)
        guard table.selectedRowIndexes.count == 2, !closeSelectionButton.isHidden else { throw ReaderError(message: "Selezione multipla non attiva.") }
        let previewBeforeClosing = activeDocument?.id
        guard let unselected = table.view(atColumn: 0, row: 4, makeIfNecessary: true) as? DocumentCell else { throw ReaderError(message: "Riga non disponibile.") }
        unselected.closeButton.performClick(nil)
        guard documents.count == 4, table.selectedRowIndexes.count == 2, activeDocument?.id == previewBeforeClosing,
              !FileManager.default.fileExists(atPath: original[4].pdfPath) else {
            throw ReaderError(message: "X non selezionata: restanti \(documents.count), selezionati \(table.selectedRowIndexes.count), anteprima mantenuta \(activeDocument?.id == previewBeforeClosing), PDF chiuso rimosso \(!FileManager.default.fileExists(atPath: original[4].pdfPath)).")
        }
        guard let selected = table.view(atColumn: 0, row: 0, makeIfNecessary: true) as? DocumentCell else { throw ReaderError(message: "Riga selezionata non disponibile.") }
        selected.closeButton.performClick(nil)
        guard documents.map(\.id) == [original[2].id, original[3].id], opened[source]?.count == 2,
              pdf.document != nil else { throw ReaderError(message: "Chiusura multipla con X non riuscita.") }
        table.selectRowIndexes(IndexSet([0, 1]), byExtendingSelection: false)
        closeSelectionButton.performClick(nil)
        guard documents.isEmpty, origins.isEmpty, opened.isEmpty, pdf.document == nil, closeSelectionButton.isHidden,
              original.allSatisfy({ !FileManager.default.fileExists(atPath: $0.pdfPath) }) else { throw ReaderError(message: "Chiusura dei selezionati incompleta.") }
    }
}
