import AppKit
import QuickLookUI

@main final class QuickLookHost: NSObject, NSApplicationDelegate {
    private var window: NSWindow!
    private var preview: QLPreviewView!
    static func main() {
        let app = NSApplication.shared; let host = QuickLookHost()
        app.delegate = host; app.setActivationPolicy(.regular)
        withExtendedLifetime(host) { app.run() }
    }
    func applicationDidFinishLaunching(_ notification: Notification) {
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 700, height: 850),
            styleMask: [.titled, .closable, .resizable], backing: .buffered, defer: false)
        window.title = "Verifica Quick Look"
        preview = QLPreviewView(frame: window.contentView!.bounds, style: .normal)
        preview.autoresizingMask = [.width, .height]; preview.autostarts = true
        window.contentView!.addSubview(preview)
        preview.previewItem = NSURL(fileURLWithPath: CommandLine.arguments[1])
        window.center(); window.makeKeyAndOrderFront(nil)
        DispatchQueue.main.asyncAfter(deadline: .now() + 3) {
            self.preview.close(); NSApp.terminate(nil)
        }
    }
}
