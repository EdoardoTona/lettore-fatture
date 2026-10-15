import Foundation
import PDFKit
import Darwin

struct PreviewRenderError: LocalizedError {
    let message: String
    var errorDescription: String? { message }
}
private struct PreviewManifest: Decodable {
    struct Document: Decodable { let kind: String; let pdfPath: String }
    let documents: [Document]
}

enum PreviewRenderer {
    private typealias RenderFunction = @convention(c) (UnsafePointer<CChar>, UnsafePointer<CChar>) -> Int32
    private static let coreLock = NSLock()
    private static var nativeCore: (UnsafeMutableRawPointer, RenderFunction)?
    private static func generate(input: URL, output: URL, bundle: URL) throws {
        coreLock.lock(); defer { coreLock.unlock() }
        if nativeCore == nil {
            let library = bundle.appendingPathComponent("Contents/Frameworks/FatturePreviewCore.dylib")
            guard let handle = dlopen(library.path, RTLD_NOW | RTLD_LOCAL), let symbol = dlsym(handle, "fatture_preview") else {
                throw PreviewRenderError(message: "Motore di anteprima non disponibile.")
            }
            // NativeAOT libraries remain loaded for the extension's lifetime.
            nativeCore = (handle, unsafeBitCast(symbol, to: RenderFunction.self))
        }
        let status = input.path.withCString { source in
            output.path.withCString { destination in nativeCore!.1(source, destination) }
        }
        guard status == 0 else {
            let message = try? String(contentsOf: output.appendingPathComponent("error.txt"), encoding: .utf8)
            throw PreviewRenderError(message: message ?? "Impossibile generare l’anteprima del documento.")
        }
    }
    static func render(file: URL, extensionBundle: URL) throws -> Data {
        let scoped = file.startAccessingSecurityScopedResource()
        defer { if scoped { file.stopAccessingSecurityScopedResource() } }
        let size = try file.resourceValues(forKeys: [.fileSizeKey]).fileSize ?? 0
        guard size <= 64 * 1024 * 1024 else { throw PreviewRenderError(message: "Il documento supera il limite di 64 MB.") }
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("FatturePreview-" + UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        defer { try? FileManager.default.removeItem(at: directory) }
        let input = directory.appendingPathComponent(file.lastPathComponent)
        try FileManager.default.copyItem(at: file, to: input)
        let output = directory.appendingPathComponent("pdf")
        try generate(input: input, output: output, bundle: extensionBundle)
        let result = try JSONDecoder().decode(PreviewManifest.self, from: Data(contentsOf: output.appendingPathComponent("manifest.json")))
        let combined = PDFDocument()
        for document in result.documents {
            let source = URL(fileURLWithPath: document.pdfPath).standardizedFileURL
            guard source.path.hasPrefix(directory.standardizedFileURL.path + "/"), let pdf = PDFDocument(url: source) else {
                throw PreviewRenderError(message: "PDF di anteprima non disponibile.")
            }
            for index in 0..<pdf.pageCount {
                guard combined.pageCount < 500 else { throw PreviewRenderError(message: "Anteprima limitata a 500 pagine. Apri il documento nell’app.") }
                if let page = pdf.page(at: index)?.copy() as? PDFPage { combined.insert(page, at: combined.pageCount) }
            }
        }
        guard combined.pageCount > 0, let data = combined.dataRepresentation() else {
            throw PreviewRenderError(message: "Nessun documento visualizzabile.")
        }
        return data
    }

    static func isInvoiceXML(_ file: URL) throws -> Bool {
        let scoped = file.startAccessingSecurityScopedResource()
        defer { if scoped { file.stopAccessingSecurityScopedResource() } }
        guard (try file.resourceValues(forKeys: [.fileSizeKey]).fileSize ?? 0) <= 64 * 1024 * 1024 else {
            throw PreviewRenderError(message: "XML troppo grande per l’anteprima.")
        }
        guard let stream = InputStream(url: file) else { throw PreviewRenderError(message: "File non leggibile.") }
        let parser = XMLParser(stream: stream); let root = RootElement()
        parser.delegate = root; parser.shouldProcessNamespaces = true; parser.shouldResolveExternalEntities = false
        parser.parse()
        return root.invoice
    }
    private final class RootElement: NSObject, XMLParserDelegate {
        var invoice = false
        func parser(_ parser: XMLParser, didStartElement elementName: String, namespaceURI: String?, qualifiedName qName: String?, attributes: [String: String]) {
            invoice = elementName == "FatturaElettronica" || elementName == "FatturaElettronicaSemplificata"
            parser.abortParsing()
        }
    }
}
