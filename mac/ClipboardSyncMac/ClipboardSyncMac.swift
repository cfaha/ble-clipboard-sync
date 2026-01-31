import Foundation
import CoreBluetooth
import AppKit
import Compression
import CryptoKit

// MARK: - UUIDs
let serviceUUID = CBUUID(string: "A1B2C3D4-0000-1000-8000-00805F9B34FB")
let notifyCharUUID = CBUUID(string: "A1B2C3D4-0001-1000-8000-00805F9B34FB")
let writeCharUUID  = CBUUID(string: "A1B2C3D4-0002-1000-8000-00805F9B34FB")

// MARK: - Config
enum SyncConfig {
    // 4-byte device id (auto-generated & persisted per device)
    static var deviceId: UInt32 = {
        let key = "BLEClipboardDeviceId"
        let defaults = UserDefaults.standard
        if let data = defaults.data(forKey: key), data.count == 4 {
            return data.withUnsafeBytes { $0.load(as: UInt32.self) }
        }
        var value: UInt32 = 0
        _ = SecRandomCopyBytes(kSecRandomDefault, 4, &value)
        var v = value
        let data = Data(bytes: &v, count: 4)
        defaults.set(data, forKey: key)
        return value
    }()

    // 16/24/32-byte key, base64 encoded. Must match on both devices.
    static let sharedKeyBase64 = "REPLACE_WITH_BASE64_KEY"

    // Compression
    static let compressionThreshold = 256
}

// MARK: - Loop Prevention State
final class LoopState {
    static var lastReceivedHash: Data?
    static var ignoreNextChange = false

    static func shouldSkip(hash: Data) -> Bool {
        if let last = lastReceivedHash, last == hash { return true }
        return false
    }

    static func markReceived(hash: Data) {
        lastReceivedHash = hash
        ignoreNextChange = true
    }
}

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
                if LoopState.ignoreNextChange {
                    LoopState.ignoreNextChange = false
                    return
                }
                self.sendClipboardIfNeeded()
            }
        }
    }

    private func sendClipboardIfNeeded() {
        if let text = pasteboard.string(forType: .string) {
            let payload = text.data(using: .utf8) ?? Data()
            let hash = CryptoHelper.sha256(payload)
            if LoopState.shouldSkip(hash: hash) { return }
            let frames = ProtocolEncoder.encode(type: 0x01, payload: payload)
            sendFrames(frames)
            return
        }

        if let tiff = pasteboard.data(forType: .tiff),
           let imageRep = NSBitmapImageRep(data: tiff),
           let png = imageRep.representation(using: .png, properties: [:]) {
            let hash = CryptoHelper.sha256(png)
            if LoopState.shouldSkip(hash: hash) { return }
            let frames = ProtocolEncoder.encode(type: 0x02, payload: png)
            sendFrames(frames)
            return
        }

        if let urls = pasteboard.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) as? [URL],
           let first = urls.first,
           let fileData = try? Data(contentsOf: first) {
            let nameData = first.lastPathComponent.data(using: .utf8) ?? Data()
            var payload = Data()
            let nameLen = UInt16(nameData.count)
            payload.append(UInt8(nameLen >> 8))
            payload.append(UInt8(nameLen & 0xff))
            payload.append(nameData)
            payload.append(fileData)
            let hash = CryptoHelper.sha256(payload)
            if LoopState.shouldSkip(hash: hash) { return }
            let frames = ProtocolEncoder.encode(type: 0x03, payload: payload)
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

// MARK: - Helpers
enum CryptoHelper {
    static var key: SymmetricKey? {
        guard let keyData = Data(base64Encoded: SyncConfig.sharedKeyBase64),
              [16, 24, 32].contains(keyData.count) else { return nil }
        return SymmetricKey(data: keyData)
    }

    static func sha256(_ data: Data) -> Data {
        let hash = SHA256.hash(data: data)
        return Data(hash)
    }

    static func encrypt(_ data: Data, aad: Data) -> Data? {
        guard let key = key else { return nil }
        let nonce = AES.GCM.Nonce()
        do {
            let sealed = try AES.GCM.seal(data, using: key, nonce: nonce, authenticating: aad)
            var out = Data()
            out.append(contentsOf: nonce)
            out.append(sealed.ciphertext)
            out.append(sealed.tag)
            return out
        } catch {
            return nil
        }
    }

    static func decrypt(_ data: Data, aad: Data) -> Data? {
        guard let key = key, data.count > 12 + 16 else { return nil }
        let nonceData = data.subdata(in: 0..<12)
        let tag = data.subdata(in: (data.count - 16)..<data.count)
        let cipher = data.subdata(in: 12..<(data.count - 16))
        do {
            let nonce = try AES.GCM.Nonce(data: nonceData)
            let sealed = try AES.GCM.SealedBox(nonce: nonce, ciphertext: cipher, tag: tag)
            return try AES.GCM.open(sealed, using: key, authenticating: aad)
        } catch {
            return nil
        }
    }
}

enum CompressionHelper {
    static func compress(_ data: Data) -> Data? {
        if data.isEmpty { return Data([0, 0, 0, 0]) }
        let dstSize = data.count + 64
        var dst = Data(count: dstSize)
        let compressedSize = dst.withUnsafeMutableBytes { dstPtr in
            data.withUnsafeBytes { srcPtr in
                compression_encode_buffer(
                    dstPtr.bindMemory(to: UInt8.self).baseAddress!,
                    dstSize,
                    srcPtr.bindMemory(to: UInt8.self).baseAddress!,
                    data.count,
                    nil,
                    COMPRESSION_ZLIB
                )
            }
        }
        if compressedSize == 0 { return nil }
        var out = Data()
        let len = UInt32(data.count)
        out.append(UInt8((len >> 24) & 0xff))
        out.append(UInt8((len >> 16) & 0xff))
        out.append(UInt8((len >> 8) & 0xff))
        out.append(UInt8(len & 0xff))
        out.append(dst.subdata(in: 0..<compressedSize))
        return out
    }

    static func decompress(_ data: Data) -> Data? {
        guard data.count >= 4 else { return nil }
        let expectedLen = Int(data[0]) << 24 | Int(data[1]) << 16 | Int(data[2]) << 8 | Int(data[3])
        let comp = data.subdata(in: 4..<data.count)
        var dst = Data(count: expectedLen)
        let decoded = dst.withUnsafeMutableBytes { dstPtr in
            comp.withUnsafeBytes { srcPtr in
                compression_decode_buffer(
                    dstPtr.bindMemory(to: UInt8.self).baseAddress!,
                    expectedLen,
                    srcPtr.bindMemory(to: UInt8.self).baseAddress!,
                    comp.count,
                    nil,
                    COMPRESSION_ZLIB
                )
            }
        }
        if decoded == 0 { return nil }
        return dst.subdata(in: 0..<decoded)
    }
}

// MARK: - Protocol
struct ProtocolEncoder {
    private static let maxChunkSize = 180

    static func encode(type: UInt8, payload: Data) -> [Data] {
        var flags: UInt8 = 0
        var body = Data()
        body.append(UInt8((SyncConfig.deviceId >> 24) & 0xff))
        body.append(UInt8((SyncConfig.deviceId >> 16) & 0xff))
        body.append(UInt8((SyncConfig.deviceId >> 8) & 0xff))
        body.append(UInt8(SyncConfig.deviceId & 0xff))
        body.append(payload)

        if body.count >= SyncConfig.compressionThreshold,
           let compressed = CompressionHelper.compress(body) {
            body = compressed
            flags |= 0x02
        }

        if let encrypted = CryptoHelper.encrypt(body, aad: Data([type, flags | 0x04])) {
            body = encrypted
            flags |= 0x04
        }

        let total = UInt16(max(1, (body.count + maxChunkSize - 1) / maxChunkSize))
        var frames: [Data] = []
        for i in 0..<Int(total) {
            let start = i * maxChunkSize
            let end = min(body.count, start + maxChunkSize)
            let chunk = body.subdata(in: start..<end)
            var frame = Data()
            frame.append(type)
            let isLast = (i == Int(total) - 1)
            frame.append((isLast ? 0x01 : 0x00) | flags)
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
    private var currentFlags: UInt8 = 0
    private var chunks: [Int: Data] = [:]

    func append(frame: Data) -> (UInt8, UInt8, Data)? {
        guard frame.count >= 8 else { return nil }
        let type = frame[0]
        let flags = frame[1]
        let seq = Int(frame[2]) << 8 | Int(frame[3])
        let total = UInt16(frame[4]) << 8 | UInt16(frame[5])
        let len = Int(frame[6]) << 8 | Int(frame[7])
        guard frame.count >= 8 + len else { return nil }

        if seq == 0 || type != currentType || total != currentTotal {
            currentType = type
            currentTotal = total
            currentFlags = flags & 0x06
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
            return (type, currentFlags, combined)
        }
        return nil
    }
}

struct ProtocolDecoder {
    private static var assembler = IncomingAssembler()

    static func handleIncoming(data: Data, pasteboard: NSPasteboard) {
        guard let (type, flags, payload) = assembler.append(frame: data) else { return }
        var body = payload

        if (flags & 0x04) != 0 {
            let aad = Data([type, flags])
            guard let decrypted = CryptoHelper.decrypt(body, aad: aad) else { return }
            body = decrypted
        }

        if (flags & 0x02) != 0 {
            guard let decompressed = CompressionHelper.decompress(body) else { return }
            body = decompressed
        }

        guard body.count >= 4 else { return }
        let senderId = UInt32(body[0]) << 24 | UInt32(body[1]) << 16 | UInt32(body[2]) << 8 | UInt32(body[3])
        if senderId == SyncConfig.deviceId { return }
        let content = body.subdata(in: 4..<body.count)
        let hash = CryptoHelper.sha256(content)

        switch type {
        case 0x01:
            if let text = String(data: content, encoding: .utf8) {
                pasteboard.clearContents()
                pasteboard.setString(text, forType: .string)
                LoopState.markReceived(hash: hash)
            }
        case 0x02:
            if let image = NSImage(data: content) {
                pasteboard.clearContents()
                pasteboard.writeObjects([image])
                LoopState.markReceived(hash: hash)
            }
        case 0x03:
            guard content.count >= 2 else { return }
            let nameLen = Int(content[0]) << 8 | Int(content[1])
            guard content.count >= 2 + nameLen else { return }
            let nameData = content.subdata(in: 2..<(2 + nameLen))
            let fileData = content.subdata(in: (2 + nameLen)..<content.count)
            let filename = String(data: nameData, encoding: .utf8) ?? "clipboard.file"
            let tmp = FileManager.default.temporaryDirectory.appendingPathComponent(filename)
            try? fileData.write(to: tmp)
            pasteboard.clearContents()
            pasteboard.writeObjects([tmp as NSURL])
            LoopState.markReceived(hash: hash)
        default:
            break
        }
    }
}

// MARK: - App Entry
// 在你的 App 入口初始化：
// let _ = ClipboardPeripheral()
