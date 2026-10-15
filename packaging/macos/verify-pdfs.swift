import Foundation
import PDFKit

let directory = URL(fileURLWithPath: CommandLine.arguments[1])
let files = FileManager.default.enumerator(at: directory, includingPropertiesForKeys: nil)!
var count = 0
for case let url as URL in files where url.pathExtension == "pdf" {
    guard let doc = PDFDocument(url: url), doc.pageCount > 0 else { fatalError("PDF non leggibile: \(url.lastPathComponent)") }
    guard let text = doc.string, !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { fatalError("PDF senza testo") }
    count += 1
}
guard count > 0 else { fatalError("Nessun PDF verificato") }
print("PDFKit: \(count) PDF leggibili, con pagine e testo selezionabile")
