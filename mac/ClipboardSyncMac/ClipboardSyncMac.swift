import Foundation
import CoreBluetooth
import AppKit

// MARK: - UUIDs
let serviceUUID = CBUUID(string: "A1B2C3D4-0000-1000-8000-00805F9B34FB")
let notifyCharUUID = CBUUID(string: "A1B2C3D4-0001-1000-8000-00805F9B34FB")
let writeCharUUID  = CBUUID(string: "A1B2C3D4-0002-1000-8000-00805F9B34FB")

// MARK: - BLE Clipboard Peripheral (Mac)
final class ClipboardPeripheral: NSObject, CBPeripheralManagerDelegate {
    private var peripheralManager: CBPeripheralManager!
    private var notifyChar: CBMutableCharacteristic!
    private let pasteboard = NSPasteboard.general
    private var changeCount: Int = 0

    override init() {
        super.init()
        peripheralManager = CBPeripheralManager(delegate: self, queue: nil)
        startClipboardMonitor()
    }

    func peripheralManagerDidUpdateState(_ peripheral: CBPeripheralManager) {
        guard peripheral.state == .poweredOn else { return }
        setupService()
        startAdvertising()
    }

    private func setupService() {
        notifyChar = CBMutableCharacteristic(
            type: notifyCharUUID,
            properties: [.notify],
            value: nil,
            permissions: [.readable]
        )
        let writeChar = CBMutableCharacteristic(
            type: writeCharUUID,
            properties: [.writeWithoutResponse, .write],
            value: nil,
            permissions: [.writeable]
        )
        let service = CBMutableService(type: serviceUUID, primary: true)
        service.characteristics = [notifyChar, writeChar]
        peripheralManager.add(service)
    }

    private func startAdvertising() {
        peripheralManager.startAdvertising([
            CBAdvertisementDataLocalNameKey: "BLEClipboardSync",
            CBAdvertisementDataServiceUUIDsKey: [serviceUUID]
        ])
    }

    // MARK: - Clipboard Monitor
    private func startClipboardMonitor() {
        changeCount = pasteboard.changeCount
        Timer.scheduledTimer(withTimeInterval: 0.8, repeats: true) { _ in
            if self.pasteboard.changeCount != self.changeCount {
                self.changeCount = self.pasteboard.changeCount
                self.sendClipboardIfNeeded()
            }
        }
    }

    private func sendClipboardIfNeeded() {
        if let text = pasteboard.string(forType: .string) {
            let frames = ProtocolEncoder.encodeText(text)
            sendFrames(frames)
            return
        }

        if let tiff = pasteboard.data(forType: .tiff),
           let imageRep = NSBitmapImageRep(data: tiff),
           let png = imageRep.representation(using: .png, properties: [:]) {
            let frames = ProtocolEncoder.encodeImage(png)
            sendFrames(frames)
            return
        }

        if let urls = pasteboard.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) as? [URL],
           let first = urls.first,
           let fileData = try? Data(contentsOf: first) {
            let frames = ProtocolEncoder.encodeFile(name: first.lastPathComponent, data: fileData)
            sendFrames(frames)
            return
        }
    }

    private func sendFrames(_ frames: [Data]) {
        for frame in frames {
            _ = peripheralManager.updateValue(frame, for: notifyChar, onSubscribedCentrals: nil)
        }
    }

    // MARK: - Receive from Windows
    func peripheralManager(_ peripheral: CBPeripheralManager, didReceiveWrite requests: [CBATTRequest]) {
        for req in requests {
            if req.characteristic.uuid == writeCharUUID, let data = req.value {
                ProtocolDecoder.handleIncoming(data: data, pasteboard: pasteboard)
            }
            peripheral.respond(to: req, withResult: .success)
        }
    }
}

// MARK: - Protocol
struct ProtocolEncoder {
    private static let maxChunkSize = 180

    static func encodeText(_ text: String) -> [Data] {
        let payload = text.data(using: .utf8) ?? Data()
        return encode(type: 0x01, payload: payload)
    }

    static func encodeImage(_ data: Data) -> [Data] {
        return encode(type: 0x02, payload: data)
    }

    static func encodeFile(name: String, data: Data) -> [Data] {
        let nameData = name.data(using: .utf8) ?? Data()
        var payload = Data()
        let nameLen = UInt16(nameData.count)
        payload.append(UInt8(nameLen >> 8))
        payload.append(UInt8(nameLen & 0xff))
        payload.append(nameData)
        payload.append(data)
        return encode(type: 0x03, payload: payload)
    }

    static func encode(type: UInt8, payload: Data) -> [Data] {
        let total = UInt16(max(1, (payload.count + maxChunkSize - 1) / maxChunkSize))
        var frames: [Data] = []
        for i in 0..<Int(total) {
            let start = i * maxChunkSize
            let end = min(payload.count, start + maxChunkSize)
            let chunk = payload.subdata(in: start..<end)
            var frame = Data()
            frame.append(type)
            let isLast = (i == Int(total) - 1)
            frame.append(isLast ? 0x01 : 0x00) // flags: last
            frame.append(UInt8((i >> 8) & 0xff))
            frame.append(UInt8(i & 0xff))
            frame.append(UInt8((Int(total) >> 8) & 0xff))
            frame.append(UInt8(Int(total) & 0xff))
            let len = UInt16(chunk.count)
            frame.append(UInt8(len >> 8))
            frame.append(UInt8(len & 0xff))
            frame.append(chunk)
            frames.append(frame)
        }
        return frames
    }
}

final class IncomingAssembler {
    private var currentType: UInt8 = 0
    private var currentTotal: UInt16 = 0
    private var chunks: [Int: Data] = [:]

    func append(frame: Data) -> (UInt8, Data)? {
        guard frame.count >= 8 else { return nil }
        let type = frame[0]
        let seq = Int(frame[2]) << 8 | Int(frame[3])
        let total = UInt16(frame[4]) << 8 | UInt16(frame[5])
        let len = Int(frame[6]) << 8 | Int(frame[7])
        guard frame.count >= 8 + len else { return nil }

        if seq == 0 || type != currentType || total != currentTotal {
            currentType = type
            currentTotal = total
            chunks.removeAll()
        }

        let payload = frame.subdata(in: 8..<(8 + len))
        chunks[seq] = payload

        if chunks.count == Int(total) {
            var combined = Data()
            for i in 0..<Int(total) {
                if let part = chunks[i] { combined.append(part) }
            }
            chunks.removeAll()
            return (type, combined)
        }
        return nil
    }
}

struct ProtocolDecoder {
    private static var assembler = IncomingAssembler()

    static func handleIncoming(data: Data, pasteboard: NSPasteboard) {
        guard let (type, payload) = assembler.append(frame: data) else { return }
        switch type {
        case 0x01:
            if let text = String(data: payload, encoding: .utf8) {
                pasteboard.clearContents()
                pasteboard.setString(text, forType: .string)
            }
        case 0x02:
            if let image = NSImage(data: payload) {
                pasteboard.clearContents()
                pasteboard.writeObjects([image])
            }
        case 0x03:
            guard payload.count >= 2 else { return }
            let nameLen = Int(payload[0]) << 8 | Int(payload[1])
            guard payload.count >= 2 + nameLen else { return }
            let nameData = payload.subdata(in: 2..<(2 + nameLen))
            let fileData = payload.subdata(in: (2 + nameLen)..<payload.count)
            let filename = String(data: nameData, encoding: .utf8) ?? "clipboard.file"
            let tmp = FileManager.default.temporaryDirectory.appendingPathComponent(filename)
            try? fileData.write(to: tmp)
            pasteboard.clearContents()
            pasteboard.writeObjects([tmp as NSURL])
        default:
            break
        }
    }
}

// MARK: - App Entry
// 在你的 App 入口初始化：
// let _ = ClipboardPeripheral()
