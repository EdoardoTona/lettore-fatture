import Foundation

struct Metadata: Codable { let label: String; let value: String }
struct SignatureInfo: Codable {
    let subject: String; let issuer: String; let signedAt: String?
    let validFrom: String; let validUntil: String; let integrity: String; let format: String
}
struct AccountingData: Codable { let fingerprint: String; let values: [String: String] }
struct ExcelExportResult: Decodable { let invoiceCount: Int; let duplicateCount: Int; let path: String }
struct Document: Codable {
    let id: String; let kind: String; let title: String; let subtitle: String
    let sourceName: String; let pdfPath: String; let suggestedFileName: String
    let metadata: [Metadata]; let signatures: [SignatureInfo]
    var invoice: AccountingData? = nil
    var symbol: String { kind == "invoice" ? "doc.text" : kind == "email" ? "envelope" : "doc.richtext" }
    var kindLabel: String { kind == "invoice" ? "Fattura" : kind == "email" ? "Email" : "PDF" }
}
struct OpenResult: Decodable { let documents: [Document]; let warnings: [String] }
struct ReaderError: LocalizedError {
    let message: String
    var errorDescription: String? { message }
}
