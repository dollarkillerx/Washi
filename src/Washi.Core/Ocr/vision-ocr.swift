// SPDX-License-Identifier: AGPL-3.0-or-later
// Washi OCR helper for macOS: Apple's Vision text recognition, on the device.
// Usage: vision-ocr <image.png> [lang,lang,...] [fast|accurate]
// Prints JSON: {"lines":[{"text":…,"x":…,"y":…,"w":…,"h":…,"conf":…}]} with the box in
// image pixels, top-left origin. Compiled once by Washi and cached.
import Foundation
import Vision
import ImageIO

func fail(_ message: String) -> Never {
    FileHandle.standardError.write((message + "\n").data(using: .utf8)!)
    exit(1)
}

let args = CommandLine.arguments
if args.count < 2 { fail("usage: vision-ocr <image> [languages] [fast|accurate]") }
if args[1] == "--languages" {
    let r = VNRecognizeTextRequest()
    r.recognitionLevel = .accurate
    let langs = (try? r.supportedRecognitionLanguages()) ?? []
    print(langs.joined(separator: ","))
    exit(0)
}
guard let src = CGImageSourceCreateWithURL(URL(fileURLWithPath: args[1]) as CFURL, nil),
      let image = CGImageSourceCreateImageAtIndex(src, 0, nil) else { fail("cannot read image \(args[1])") }
let width = Double(image.width), height = Double(image.height)

let request = VNRecognizeTextRequest()
request.recognitionLevel = args.count > 3 && args[3] == "fast" ? .fast : .accurate
request.usesLanguageCorrection = true
if args.count > 2 && !args[2].isEmpty {
    let supported = Set((try? request.supportedRecognitionLanguages()) ?? [])
    let wanted = args[2].split(separator: ",").map(String.init).filter { supported.contains($0) }
    if !wanted.isEmpty { request.recognitionLanguages = wanted }
    if #available(macOS 13.0, *) { request.automaticallyDetectsLanguage = wanted.isEmpty }
}

do {
    try VNImageRequestHandler(cgImage: image, options: [:]).perform([request])
} catch {
    fail("recognition failed: \(error.localizedDescription)")
}

var lines: [[String: Any]] = []
for observation in request.results ?? [] {
    guard let best = observation.topCandidates(1).first else { continue }
    let b = observation.boundingBox   // normalized, bottom-left origin
    lines.append([
        "text": best.string,
        "x": b.minX * width, "y": (1 - b.maxY) * height,
        "w": b.width * width, "h": b.height * height,
        "conf": Double(best.confidence),
    ])
}
let data = try! JSONSerialization.data(withJSONObject: ["lines": lines], options: [])
FileHandle.standardOutput.write(data)
