import Foundation
import QuickLookUI
import UniformTypeIdentifiers
import OSLog

@objc(FatturePreviewProvider)
final class PreviewProvider: QLPreviewProvider, QLPreviewingController {
    private static let log = Logger(subsystem: "it.edoardotona.lettorefatture.preview", category: "Rendering")
    func providePreview(for request: QLFilePreviewRequest, completionHandler handler: @escaping (QLPreviewReply?, Error?) -> Void) {
        let file = request.fileURL
        Self.log.info("Quick Look preview requested")
        let bundle = Bundle(for: PreviewProvider.self).bundleURL
        // XML is a generic file type: leave non-invoice XML readable as text.
        if file.pathExtension.lowercased() == "xml", (try? PreviewRenderer.isInvoiceXML(file)) != true {
            let reply = QLPreviewReply(dataOfContentType: .plainText, contentSize: CGSize(width: 700, height: 900)) { _ in
                let scoped = file.startAccessingSecurityScopedResource()
                defer { if scoped { file.stopAccessingSecurityScopedResource() } }
                let size = try file.resourceValues(forKeys: [.fileSizeKey]).fileSize ?? 0
                guard size <= 64 * 1024 * 1024 else { throw PreviewRenderError(message: "XML troppo grande per l’anteprima.") }
                let data = try Data(contentsOf: file)
                let text = String(data: data, encoding: .utf8) ?? String(data: data, encoding: .utf16) ?? "XML non leggibile come testo."
                Self.log.info("Quick Look XML text ready")
                return Data(text.utf8)
            }
            reply.title = file.lastPathComponent; handler(reply, nil); return
        }
        let reply = QLPreviewReply(dataOfContentType: .pdf, contentSize: CGSize(width: 595, height: 842)) { _ in
            do {
                let data = try PreviewRenderer.render(file: file, extensionBundle: bundle)
                Self.log.info("Quick Look PDF ready")
                return data
            } catch {
                Self.log.error("Quick Look PDF generation failed: \(error.localizedDescription, privacy: .public)")
                throw error
            }
        }
        reply.title = file.lastPathComponent
        handler(reply, nil)
    }
}
