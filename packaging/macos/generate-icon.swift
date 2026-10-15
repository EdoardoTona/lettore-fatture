import AppKit

let destination = CommandLine.arguments[1]
try FileManager.default.createDirectory(atPath: destination, withIntermediateDirectories: true)
for size in [16, 32, 128, 256, 512] {
    for scale in [1, 2] {
        let pixels = size * scale
        // Render in physical pixels, independent of the Mac's Retina scale.
        let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: pixels, pixelsHigh: pixels,
            bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
            colorSpaceName: .deviceRGB, bytesPerRow: pixels * 4, bitsPerPixel: 32)!
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: bitmap)
        let s = CGFloat(pixels)
        let background = NSBezierPath(roundedRect: NSRect(x: s * 0.06, y: s * 0.06, width: s * 0.88, height: s * 0.88), xRadius: s * 0.2, yRadius: s * 0.2)
        NSColor(calibratedRed: 0.11, green: 0.36, blue: 0.4, alpha: 1).setFill(); background.fill()
        let paper = NSBezierPath(roundedRect: NSRect(x: s * 0.28, y: s * 0.2, width: s * 0.44, height: s * 0.6), xRadius: s * 0.035, yRadius: s * 0.035)
        NSColor.white.setFill(); paper.fill()
        NSColor(calibratedRed: 0.11, green: 0.36, blue: 0.4, alpha: 0.6).setStroke()
        for y in [0.63, 0.52, 0.41] {
            let line = NSBezierPath(); line.lineWidth = s * 0.025
            line.move(to: NSPoint(x: s * 0.36, y: s * y)); line.line(to: NSPoint(x: s * 0.64, y: s * y)); line.stroke()
        }
        NSGraphicsContext.restoreGraphicsState()
        let suffix = scale == 2 ? "@2x" : ""
        try bitmap.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: "\(destination)/icon_\(size)x\(size)\(suffix).png"))
    }
}
