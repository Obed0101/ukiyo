// Renders the brand app icon (.github/assets/brand/ukiyo-app-icon.svg: a 1024 viewBox, one rounded rect and a list of
// square <rect>s) into the PNG sizes of an .iconset. Reads the SVG's own geometry, so the icon stays the brand mark.
// Usage: swiftc IconMaker.swift -o icon-maker && icon-maker <app-icon.svg> <out.iconset>
import AppKit

guard CommandLine.arguments.count == 3,
      let svg = try? String(contentsOfFile: CommandLine.arguments[1], encoding: .utf8) else {
    FileHandle.standardError.write(Data("usage: icon-maker <app-icon.svg> <out.iconset>\n".utf8))
    exit(2)
}
let outDirectory = CommandLine.arguments[2]
try FileManager.default.createDirectory(atPath: outDirectory, withIntermediateDirectories: true)

func attributes(of tag: String) -> [String: Double] {
    var result: [String: Double] = [:]
    let pattern = try! NSRegularExpression(pattern: #"(\w+)="(-?[0-9.]+)""#)
    for match in pattern.matches(in: tag, range: NSRange(tag.startIndex..., in: tag)) {
        if let key = Range(match.range(at: 1), in: tag), let value = Range(match.range(at: 2), in: tag), let number = Double(tag[value]) {
            result[String(tag[key])] = number
        }
    }
    return result
}

let rectTags = try! NSRegularExpression(pattern: #"<rect[^>]*/>"#)
let rects = rectTags.matches(in: svg, range: NSRange(svg.startIndex..., in: svg)).compactMap { Range($0.range, in: svg).map { String(svg[$0]) } }
guard let background = rects.first.map(attributes(of:)) else { fatalError("[ICON]: no <rect> in the SVG") }
let squares = rects.dropFirst().map(attributes(of:))
let corner = background["rx"] ?? 0

/// Mac icons keep ~10% transparent margin around the plate (824 of 1024).
func render(pixels: Int) -> Data {
    let size = CGFloat(pixels)
    let image = NSImage(size: NSSize(width: size, height: size))
    image.lockFocus()
    let context = NSGraphicsContext.current!.cgContext
    let plate = size * 824 / 1024
    let inset = (size - plate) / 2
    let scale = plate / 1024
    context.translateBy(x: inset, y: size - inset)
    context.scaleBy(x: scale, y: -scale) // SVG space: y down

    context.saveGState()
    context.setShadow(offset: CGSize(width: 0, height: -10), blur: 24, color: NSColor.black.withAlphaComponent(0.35).cgColor)
    let plateRect = CGRect(x: 0, y: 0, width: 1024, height: 1024)
    context.addPath(CGPath(roundedRect: plateRect, cornerWidth: corner, cornerHeight: corner, transform: nil))
    context.setFillColor(NSColor(srgbRed: 0x0a / 255, green: 0x0a / 255, blue: 0x0a / 255, alpha: 1).cgColor)
    context.fillPath()
    context.restoreGState()

    context.setShouldAntialias(false)
    context.setFillColor(NSColor.white.cgColor)
    for square in squares {
        context.fill(CGRect(x: square["x"] ?? 0, y: square["y"] ?? 0, width: square["width"] ?? 0, height: square["height"] ?? 0))
    }
    image.unlockFocus()
    let bitmap = NSBitmapImageRep(data: image.tiffRepresentation!)!
    return bitmap.representation(using: .png, properties: [:])!
}

// iconutil's required names: icon_<size>x<size>[@2x].png
for base in [16, 32, 128, 256, 512] {
    try render(pixels: base).write(to: URL(fileURLWithPath: "\(outDirectory)/icon_\(base)x\(base).png"))
    try render(pixels: base * 2).write(to: URL(fileURLWithPath: "\(outDirectory)/icon_\(base)x\(base)@2x.png"))
}
