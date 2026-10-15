import AppKit
import PDFKit

final class DropView: NSView {
    var onDrop: (([URL]) -> Void)?
    private var highlighted = false
    override init(frame: NSRect) { super.init(frame: frame); registerForDraggedTypes([.fileURL]); wantsLayer = true }
    required init?(coder: NSCoder) { fatalError() }
    private func urls(_ sender: NSDraggingInfo) -> [URL] {
        (sender.draggingPasteboard.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) as? [URL] ?? [])
            .filter { ["xml", "p7m", "eml", "pdf"].contains($0.pathExtension.lowercased()) }
    }
    override func draggingEntered(_ sender: NSDraggingInfo) -> NSDragOperation {
        highlighted = !urls(sender).isEmpty; needsDisplay = true; return highlighted ? .copy : []
    }
    override func draggingExited(_ sender: NSDraggingInfo?) { highlighted = false; needsDisplay = true }
    override func performDragOperation(_ sender: NSDraggingInfo) -> Bool {
        let files = urls(sender); highlighted = false; needsDisplay = true
        guard !files.isEmpty else { return false }; onDrop?(files); return true
    }
    override func draw(_ dirtyRect: NSRect) {
        super.draw(dirtyRect)
        if highlighted {
            NSColor.controlAccentColor.withAlphaComponent(0.12).setFill(); bounds.fill()
            NSColor.controlAccentColor.setStroke()
            let border = NSBezierPath(roundedRect: bounds.insetBy(dx: 4, dy: 4), xRadius: 10, yRadius: 10)
            border.lineWidth = 3; border.stroke()
        }
    }
}

final class DocumentTable: NSTableView {
    var onRemove: (() -> Void)?
    override func keyDown(with event: NSEvent) {
        if event.keyCode == 51 || event.keyCode == 117 { onRemove?() } else { super.keyDown(with: event) }
    }
}

final class ReaderPDFView: PDFView {
    var onDrop: (([URL]) -> Void)?
    private func files(_ sender: NSDraggingInfo) -> [URL] {
        (sender.draggingPasteboard.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) as? [URL] ?? [])
            .filter { ["xml", "p7m", "eml", "pdf"].contains($0.pathExtension.lowercased()) }
    }
    override func draggingEntered(_ sender: NSDraggingInfo) -> NSDragOperation { files(sender).isEmpty ? [] : .copy }
    override func draggingUpdated(_ sender: NSDraggingInfo) -> NSDragOperation { draggingEntered(sender) }
    override func prepareForDragOperation(_ sender: NSDraggingInfo) -> Bool { !files(sender).isEmpty }
    override func performDragOperation(_ sender: NSDraggingInfo) -> Bool {
        let urls = files(sender); guard !urls.isEmpty else { return false }; onDrop?(urls); return true
    }
}

final class DocumentCell: NSTableCellView {
    let title = NSTextField(labelWithString: "")
    let detail = NSTextField(labelWithString: "")
    let symbol = NSImageView()
    let closeButton = NSButton()
    var onClose: (() -> Void)?
    override init(frame: NSRect) {
        super.init(frame: frame)
        title.font = .systemFont(ofSize: 12, weight: .medium); title.lineBreakMode = .byTruncatingMiddle
        detail.font = .systemFont(ofSize: 10); detail.textColor = .secondaryLabelColor; detail.lineBreakMode = .byTruncatingMiddle
        symbol.contentTintColor = .secondaryLabelColor
        closeButton.image = NSImage(systemSymbolName: "xmark", accessibilityDescription: "Chiudi documento")
        closeButton.isBordered = false; closeButton.bezelStyle = .inline; closeButton.controlSize = .small
        closeButton.contentTintColor = .secondaryLabelColor; closeButton.toolTip = "Chiudi documento; se selezionato, chiude tutti i selezionati"
        closeButton.setAccessibilityLabel("Chiudi documento")
        closeButton.target = self; closeButton.action = #selector(close)
        let stack = NSStackView(views: [title, detail]); stack.orientation = .vertical; stack.alignment = .leading; stack.spacing = 4
        addSubview(symbol); addSubview(stack); addSubview(closeButton)
        symbol.translatesAutoresizingMaskIntoConstraints = false; stack.translatesAutoresizingMaskIntoConstraints = false; closeButton.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            symbol.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 8), symbol.centerYAnchor.constraint(equalTo: centerYAnchor),
            symbol.widthAnchor.constraint(equalToConstant: 22), symbol.heightAnchor.constraint(equalToConstant: 24),
            stack.leadingAnchor.constraint(equalTo: symbol.trailingAnchor, constant: 10), stack.trailingAnchor.constraint(equalTo: closeButton.leadingAnchor, constant: -6),
            closeButton.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -8), closeButton.centerYAnchor.constraint(equalTo: centerYAnchor),
            closeButton.widthAnchor.constraint(equalToConstant: 22), closeButton.heightAnchor.constraint(equalToConstant: 24),
            stack.centerYAnchor.constraint(equalTo: centerYAnchor), title.widthAnchor.constraint(equalTo: stack.widthAnchor), detail.widthAnchor.constraint(equalTo: stack.widthAnchor)
        ])
        textField = title; imageView = symbol
    }
    @objc private func close() { onClose?() }
    required init?(coder: NSCoder) { fatalError() }
}
