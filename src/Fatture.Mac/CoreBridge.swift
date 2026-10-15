import Foundation
import Darwin

private struct Response: Decodable { let id: String; let ok: Bool; let result: OpenResult?; let error: String?; let export: ExcelExportResult? }
private struct Request: Encodable {
    let id: String; let operation: String
    var path: String? = nil; var outputDirectory: String? = nil
    var documents: [Document]? = nil; var exportPath: String? = nil
}

// A single persistent worker, entirely off the main thread. The UI never waits on C#.
final class Bridge {
    private let queue = DispatchQueue(label: "it.lettorefatture.bridge", qos: .userInitiated)
    private let lifecycle = NSLock()
    private var stopped = false
    private var process: Process?
    private var processExit: DispatchSemaphore?
    private var input: FileHandle?
    private var output: FileHandle?
    private var buffer = Data()
    func open(_ url: URL, outputDirectory: URL, completion: @escaping (Result<OpenResult, Error>) -> Void) {
        send(Request(id: UUID().uuidString, operation: "open", path: url.path, outputDirectory: outputDirectory.path)) { response in
            completion(response.flatMap { value in
                if let result = value.result { return .success(result) }
                return .failure(ReaderError(message: "Il motore non ha restituito i documenti."))
            })
        }
    }
    func exportExcel(_ documents: [Document], to url: URL, completion: @escaping (Result<ExcelExportResult, Error>) -> Void) {
        send(Request(id: UUID().uuidString, operation: "exportExcel", documents: documents, exportPath: url.path)) { response in
            completion(response.flatMap { value in
                if let result = value.export { return .success(result) }
                return .failure(ReaderError(message: "Il motore non ha restituito l’esito dell’export."))
            })
        }
    }
    private func send(_ request: Request, completion: @escaping (Result<Response, Error>) -> Void) {
        queue.async {
            let result: Result<Response, Error>
            do {
                try self.start()
                var data = try JSONEncoder().encode(request); data.append(10)
                try self.input!.write(contentsOf: data)
                let response = try JSONDecoder().decode(Response.self, from: self.readLine())
                guard response.id == request.id else { throw ReaderError(message: "Risposta inattesa dal motore di lettura.") }
                guard response.ok else {
                    throw ReaderError(message: response.error ?? "Impossibile leggere il documento.")
                }
                result = .success(response)
            } catch {
                result = .failure(error)
                self.lifecycle.lock()
                if self.process?.isRunning != true { self.process = nil }
                self.lifecycle.unlock()
            }
            DispatchQueue.main.async {
                self.lifecycle.lock(); let stopped = self.stopped; self.lifecycle.unlock()
                if !stopped { completion(result) }
            }
        }
    }
    private func start() throws {
        lifecycle.lock(); defer { lifecycle.unlock() }
        guard !stopped else { throw ReaderError(message: "L’app si sta chiudendo.") }
        if process?.isRunning == true { return }
        let url = Bundle.main.resourceURL!.appendingPathComponent("bridge/fatture")
        guard FileManager.default.isExecutableFile(atPath: url.path) else {
            throw ReaderError(message: "Motore C# non trovato. Ricrea l’app con packaging/macos/package.sh.")
        }
        let worker = Process(); let stdin = Pipe(); let stdout = Pipe()
        worker.executableURL = url; worker.arguments = ["bridge"]
        worker.standardInput = stdin; worker.standardOutput = stdout; worker.standardError = FileHandle.nullDevice
        var environment = ProcessInfo.processInfo.environment
        environment["DOTNET_EnableDiagnostics"] = "0"
        worker.environment = environment
        let exited = DispatchSemaphore(value: 0)
        worker.terminationHandler = { _ in exited.signal() }
        try worker.run()
        process = worker; processExit = exited
        input = stdin.fileHandleForWriting; output = stdout.fileHandleForReading; buffer.removeAll()
    }
    private func readLine() throws -> Data {
        while true {
            if let index = buffer.firstIndex(of: 10) {
                let line = Data(buffer[..<index]); buffer.removeSubrange(...index); return line
            }
            // read(upToCount:) may wait for the entire count on a persistent pipe.
            // availableData returns the current response chunk without waiting for EOF.
            guard let chunk = output?.availableData, !chunk.isEmpty else {
                throw ReaderError(message: "Il motore di lettura si è interrotto. Riprova ad aprire il documento.")
            }
            buffer.append(chunk)
            if buffer.count > 16 * 1024 * 1024 { throw ReaderError(message: "Risposta del motore troppo grande.") }
        }
    }
    func stop() {
        // Serialize shutdown with worker creation so closing during an import
        // cannot leave a newly launched core behind or let pending opens restart it.
        lifecycle.lock()
        stopped = true
        let worker = process; let exited = processExit
        lifecycle.unlock()
        if let worker {
            if worker.isRunning {
                worker.terminate()
                if exited?.wait(timeout: .now() + 2) == .timedOut {
                    kill(worker.processIdentifier, SIGKILL)
                }
            }
            worker.waitUntilExit()
        }
        // The worker has exited: finish pending reads before removing session files.
        queue.sync {
            try? input?.close(); try? output?.close()
            input = nil; output = nil; buffer.removeAll()
            lifecycle.lock(); process = nil; processExit = nil; lifecycle.unlock()
        }
    }
}
