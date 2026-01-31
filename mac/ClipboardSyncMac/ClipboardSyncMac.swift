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
    // 8-byte device id (auto-generated & persisted per device)
    static var deviceId: UInt64 = {
        let key = "BLEClipboardDeviceId"
        let defaults = UserDefaults.standard
        if let data = defaults.data(forKey: key), data.count == 8 {
            return data.withUnsafeBytes { $0.load(as: UInt64.self) }
        }
        var value: UInt64 = 0
        _ = SecRandomCopyBytes(kSecRandomDefault, 8, &value)
        var v = value
        let data = Data(bytes: &v, count: 8)
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

// MARK: - Device Trust
final class DeviceTrustCenter {
    static let shared = DeviceTrustCenter()
    private let key = "BLEClipboardTrustedDevices"
    private let aliasKey = "BLEClipboardTrustedDeviceAliases"
    private var trusted: Set<UInt64> = []
    private var aliases: [UInt64: String] = [:]
    private var allowNextUnknown = false
    var onChange: (() -> Void)?

    private init() {
        load()
    }

    func isTrusted(_ id: UInt64) -> Bool {
        return trusted.contains(id)
    }

    func allTrusted() -> [UInt64] {
        return trusted.sorted()
    }

    func add(_ id: UInt64) {
        guard !trusted.contains(id) else { return }
        trusted.insert(id)
        save()
    }

    func remove(_ id: UInt64) {
        guard trusted.contains(id) else { return }
        trusted.remove(id)
        aliases.removeValue(forKey: id)
        save()
    }

    func clear() {
        guard !trusted.isEmpty else { return }
        trusted.removeAll()
        aliases.removeAll()
        save()
    }

    func alias(for id: UInt64) -> String? {
        return aliases[id]
    }

    func setAlias(_ id: UInt64, alias: String?) {
        let trimmed = alias?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        if trimmed.isEmpty {
            if aliases.removeValue(forKey: id) != nil { save() }
            return
        }
        if aliases[id] != trimmed {
            aliases[id] = trimmed
            save()
        }
    }

    func displayName(_ id: UInt64) -> String {
        if let alias = aliases[id], !alias.isEmpty {
            return "\(alias) (\(DeviceTrustCenter.format(id)))"
        }
        return DeviceTrustCenter.format(id)
    }

    func ensureTrusted(_ id: UInt64) -> Bool {
        if isTrusted(id) { return true }
        if allowNextUnknown {
            allowNextUnknown = false
            add(id)
            return true
        }
        let allowed = promptTrust(id)
        if allowed { add(id) }
        return allowed
    }

    func promptOnConnect(_ centralId: UUID) {
        let prompt = {
            let alert = NSAlert()
            alert.messageText = "允许此设备连接？"
            alert.informativeText = "检测到新连接: \(centralId.uuidString)\n是否允许后续剪贴板同步？"
            alert.addButton(withTitle: "允许")
            alert.addButton(withTitle: "拒绝")
            let response = alert.runModal()
            return response == .alertFirstButtonReturn
        }
        let allowed: Bool
        if Thread.isMainThread { allowed = prompt() }
        else {
            var ok = false
            DispatchQueue.main.sync { ok = prompt() }
            allowed = ok
        }
        allowNextUnknown = allowed
    }

    private func load() {
        let defaults = UserDefaults.standard
        if let list = defaults.array(forKey: key) as? [String] {
            trusted = Set(list.compactMap { UInt64($0) })
        }
        if let dict = defaults.dictionary(forKey: aliasKey) as? [String: String] {
            var mapped: [UInt64: String] = [:]
            for (k, v) in dict {
                if let id = UInt64(k), !v.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                    mapped[id] = v
                }
            }
            aliases = mapped
        }
    }

    private func save() {
        let defaults = UserDefaults.standard
        let list = trusted.map { String($0) }
        defaults.set(list, forKey: key)
        var dict: [String: String] = [:]
        for (id, alias) in aliases {
            let trimmed = alias.trimmingCharacters(in: .whitespacesAndNewlines)
            if !trimmed.isEmpty {
                dict[String(id)] = trimmed
            }
        }
        defaults.set(dict, forKey: aliasKey)
        onChange?()
    }

    private func promptTrust(_ id: UInt64) -> Bool {
        let prompt = {
            let alert = NSAlert()
            alert.messageText = "信任此设备？"
            alert.informativeText = "检测到新的设备: \(displayName(id))\n是否允许与其同步剪贴板？"
            alert.addButton(withTitle: "信任")
            alert.addButton(withTitle: "拒绝")
            let response = alert.runModal()
            return response == .alertFirstButtonReturn
        }
        if Thread.isMainThread { return prompt() }
        var allowed = false
        DispatchQueue.main.sync { allowed = prompt() }
        return allowed
    }

    static func format(_ id: UInt64) -> String {
        return String(format: "%016llX", id)
    }
}

// MARK: - Status
final class StatusCenter {
    enum State: String {
        case disconnected = "未连接"
        case connected = "已连接"
        case encrypted = "已连接·已加密"
        case transferring = "传输中"
    }

    static let shared = StatusCenter()
    private var state: State = .disconnected
    private var lastStable: State = .disconnected
    private var resetTimer: Timer?

    var onUpdate: ((State) -> Void)?

    func set(_ newState: State) {
        state = newState
        if newState != .transferring { lastStable = newState }
        onUpdate?(state)
    }

    func bumpTransfer() {
        set(.transferring)
        resetTimer?.invalidate()
        resetTimer = Timer.scheduledTimer(withTimeInterval: 1.0, repeats: false) { _ in
            self.set(self.lastStable)
        }
    }
}

// MARK: - Logs
final class LogCenter {
    static let shared = LogCenter()
    private let queue = DispatchQueue(label: "LogCenter.queue")
    private var buffer: [String] = []
    private let maxLines = 1000

    func log(_ message: String) {
        let line = "\(LogCenter.timestamp()) \(message)"
        queue.sync {
            buffer.append(line)
            if buffer.count > maxLines {
                buffer.removeFirst(buffer.count - maxLines)
            }
        }
        print(line)
    }

    func exportToFile() -> URL? {
        let contents = queue.sync { buffer.joined(separator: "\n") }
        let dir = LogCenter.logDirectory()
        do {
            try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
            let filename = "clipboard-sync-\(LogCenter.fileTimestamp()).log"
            let url = dir.appendingPathComponent(filename)
            try contents.write(to: url, atomically: true, encoding: .utf8)
            return url
        } catch {
            return nil
        }
    }

    static func logDirectory() -> URL {
        let base = FileManager.default.urls(for: .libraryDirectory, in: .userDomainMask).first!
        return base.appendingPathComponent("Logs/ClipboardSync", isDirectory: true)
    }

    private static func timestamp() -> String {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return formatter.string(from: Date())
    }

    private static func fileTimestamp() -> String {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = "yyyyMMdd-HHmmss"
        return formatter.string(from: Date())
    }
}

// MARK: - Auto Launch
enum AutoLaunchManager {
    private static var label: String {
        return Bundle.main.bundleIdentifier ?? "com.ble.clipboardsync"
    }

    private static var plistURL: URL {
        let home = FileManager.default.homeDirectoryForCurrentUser
        return home.appendingPathComponent("Library/LaunchAgents/\(label).plist")
    }

    static func isEnabled() -> Bool {
        return FileManager.default.fileExists(atPath: plistURL.path)
    }

    static func setEnabled(_ enabled: Bool) {
        if enabled {
            createPlistIfNeeded()
            runLaunchctl(["load", "-w", plistURL.path])
        } else {
            runLaunchctl(["unload", "-w", plistURL.path])
            try? FileManager.default.removeItem(at: plistURL)
        }
    }

    private static func createPlistIfNeeded() {
        let dir = plistURL.deletingLastPathComponent()
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        guard let exec = Bundle.main.executablePath else { return }
        let plist: [String: Any] = [
            "Label": label,
            "ProgramArguments": [exec],
            "RunAtLoad": true
        ]
        guard let data = try? PropertyListSerialization.data(fromPropertyList: plist, format: .xml, options: 0) else { return }
        try? data.write(to: plistURL)
    }

    private static func runLaunchctl(_ args: [String]) {
        let task = Process()
        task.launchPath = "/bin/launchctl"
        task.arguments = args
        try? task.run()
        task.waitUntilExit()
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
        LogCenter.shared.log("Peripheral init")
        StatusCenter.shared.set(.disconnected)
        peripheralManager = CBPeripheralManager(delegate: self, queue: nil)
        startClipboardMonitor()
    }

    func peripheralManagerDidUpdateState(_ peripheral: CBPeripheralManager) {
        LogCenter.shared.log("Peripheral state: \(peripheral.state.rawValue)")
        guard peripheral.state == .poweredOn else {
            StatusCenter.shared.set(.disconnected)
            return
        }
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
        LogCenter.shared.log("Start advertising BLEClipboardSync")
        peripheralManager.startAdvertising([
            CBAdvertisementDataLocalNameKey: "BLEClipboardSync",
            CBAdvertisementDataServiceUUIDsKey: [serviceUUID]
        ])
    }

    func peripheralManager(_ peripheral: CBPeripheralManager, central: CBCentral, didSubscribeTo characteristic: CBCharacteristic) {
        LogCenter.shared.log("Central subscribed: \(central.identifier.uuidString)")
        DeviceTrustCenter.shared.promptOnConnect(central.identifier)
        if CryptoHelper.key != nil {
            StatusCenter.shared.set(.encrypted)
        } else {
            StatusCenter.shared.set(.connected)
        }
    }

    func peripheralManager(_ peripheral: CBPeripheralManager, central: CBCentral, didUnsubscribeFrom characteristic: CBCharacteristic) {
        LogCenter.shared.log("Central unsubscribed: \(central.identifier.uuidString)")
        StatusCenter.shared.set(.disconnected)
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
            LogCenter.shared.log("Send text: \(payload.count) bytes, \(frames.count) frames")
            sendFrames(frames)
            return
        }

        if let tiff = pasteboard.data(forType: .tiff),
           let imageRep = NSBitmapImageRep(data: tiff),
           let png = imageRep.representation(using: .png, properties: [:]) {
            let hash = CryptoHelper.sha256(png)
            if LoopState.shouldSkip(hash: hash) { return }
            let frames = ProtocolEncoder.encode(type: 0x02, payload: png)
            LogCenter.shared.log("Send image: \(png.count) bytes, \(frames.count) frames")
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
            LogCenter.shared.log("Send file: \(payload.count) bytes, \(frames.count) frames, name=\(first.lastPathComponent)")
            sendFrames(frames)
            return
        }
    }

    private func sendFrames(_ frames: [Data]) {
        StatusCenter.shared.bumpTransfer()
        for frame in frames {
            _ = peripheralManager.updateValue(frame, for: notifyChar, onSubscribedCentrals: nil)
        }
    }

    // subscription callbacks handled above

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
        body.append(UInt8((SyncConfig.deviceId >> 56) & 0xff))
        body.append(UInt8((SyncConfig.deviceId >> 48) & 0xff))
        body.append(UInt8((SyncConfig.deviceId >> 40) & 0xff))
        body.append(UInt8((SyncConfig.deviceId >> 32) & 0xff))
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

        guard body.count >= 8 else { return }
        let senderId = UInt64(body[0]) << 56 | UInt64(body[1]) << 48 | UInt64(body[2]) << 40 | UInt64(body[3]) << 32 |
                       UInt64(body[4]) << 24 | UInt64(body[5]) << 16 | UInt64(body[6]) << 8 | UInt64(body[7])
        if senderId == SyncConfig.deviceId { return }
        guard DeviceTrustCenter.shared.ensureTrusted(senderId) else { return }
        let content = body.subdata(in: 8..<body.count)
        let hash = CryptoHelper.sha256(content)
        StatusCenter.shared.bumpTransfer()
        LogCenter.shared.log("Received type=\(type), bytes=\(content.count)")

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
final class AppDelegate: NSObject, NSApplicationDelegate {
    private var statusItem: NSStatusItem!
    private var statusMenuItem: NSMenuItem!
    private var trustedMenuItem: NSMenuItem!
    private var trustedMenu: NSMenu!
    private var autoStartMenuItem: NSMenuItem!
    private var peripheral: ClipboardPeripheral?

    func applicationDidFinishLaunching(_ notification: Notification) {
        LogCenter.shared.log("App launched")
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        statusItem.button?.title = "Clip"

        let menu = NSMenu()
        statusMenuItem = NSMenuItem(title: "Status: starting…", action: nil, keyEquivalent: "")
        statusMenuItem.isEnabled = false
        menu.addItem(statusMenuItem)

        trustedMenuItem = NSMenuItem(title: "受信任设备", action: nil, keyEquivalent: "")
        trustedMenu = NSMenu()
        trustedMenuItem.submenu = trustedMenu
        menu.addItem(trustedMenuItem)

        autoStartMenuItem = NSMenuItem(title: "开机自启", action: #selector(toggleAutoStart), keyEquivalent: "")
        autoStartMenuItem.target = self
        autoStartMenuItem.state = AutoLaunchManager.isEnabled() ? .on : .off
        menu.addItem(autoStartMenuItem)

        let exportLogItem = NSMenuItem(title: "导出日志", action: #selector(exportLogs), keyEquivalent: "")
        exportLogItem.target = self
        menu.addItem(exportLogItem)

        menu.addItem(NSMenuItem.separator())
        menu.addItem(NSMenuItem(title: "Quit", action: #selector(quitApp), keyEquivalent: "q"))
        statusItem.menu = menu

        StatusCenter.shared.onUpdate = { [weak self] state in
            let status = state.rawValue
            self?.statusMenuItem.title = "状态: \(status)"
            self?.statusItem.button?.toolTip = status
        }
        StatusCenter.shared.set(.disconnected)

        DeviceTrustCenter.shared.onChange = { [weak self] in
            self?.refreshTrustedMenu()
        }
        refreshTrustedMenu()

        peripheral = ClipboardPeripheral()
    }

    private func refreshTrustedMenu() {
        if !Thread.isMainThread {
            DispatchQueue.main.async { [weak self] in self?.refreshTrustedMenu() }
            return
        }
        trustedMenu.removeAllItems()
        let devices = DeviceTrustCenter.shared.allTrusted()
        if devices.isEmpty {
            let empty = NSMenuItem(title: "（暂无）", action: nil, keyEquivalent: "")
            empty.isEnabled = false
            trustedMenu.addItem(empty)
        } else {
            for id in devices {
                let deviceItem = NSMenuItem(title: DeviceTrustCenter.shared.displayName(id), action: nil, keyEquivalent: "")
                let submenu = NSMenu()
                let renameItem = NSMenuItem(title: "重命名…", action: #selector(renameTrustedDevice(_:)), keyEquivalent: "")
                renameItem.representedObject = id
                renameItem.target = self
                submenu.addItem(renameItem)
                let removeItem = NSMenuItem(title: "移除", action: #selector(removeTrustedDevice(_:)), keyEquivalent: "")
                removeItem.representedObject = id
                removeItem.target = self
                submenu.addItem(removeItem)
                deviceItem.submenu = submenu
                trustedMenu.addItem(deviceItem)
            }
            trustedMenu.addItem(NSMenuItem.separator())
            trustedMenu.addItem(NSMenuItem(title: "清空列表", action: #selector(clearTrustedDevices), keyEquivalent: ""))
        }
    }

    @objc private func removeTrustedDevice(_ sender: NSMenuItem) {
        if let id = sender.representedObject as? UInt64 {
            DeviceTrustCenter.shared.remove(id)
        }
    }

    @objc private func renameTrustedDevice(_ sender: NSMenuItem) {
        guard let id = sender.representedObject as? UInt64 else { return }
        let alert = NSAlert()
        alert.messageText = "重命名设备"
        alert.informativeText = "设备: \(DeviceTrustCenter.shared.displayName(id))"
        let input = NSTextField(frame: NSRect(x: 0, y: 0, width: 220, height: 24))
        input.stringValue = DeviceTrustCenter.shared.alias(for: id) ?? ""
        alert.accessoryView = input
        alert.addButton(withTitle: "保存")
        alert.addButton(withTitle: "取消")
        let response = alert.runModal()
        if response == .alertFirstButtonReturn {
            let newAlias = input.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
            DeviceTrustCenter.shared.setAlias(id, alias: newAlias.isEmpty ? nil : newAlias)
        }
    }

    @objc private func clearTrustedDevices() {
        DeviceTrustCenter.shared.clear()
    }

    @objc private func toggleAutoStart() {
        let enable = autoStartMenuItem.state != .on
        AutoLaunchManager.setEnabled(enable)
        autoStartMenuItem.state = AutoLaunchManager.isEnabled() ? .on : .off
    }

    @objc private func exportLogs() {
        let alert = NSAlert()
        if let url = LogCenter.shared.exportToFile() {
            alert.messageText = "日志已导出"
            alert.informativeText = url.path
            LogCenter.shared.log("Export logs to \(url.path)")
        } else {
            alert.messageText = "日志导出失败"
            alert.informativeText = "无法写入日志文件"
        }
        alert.addButton(withTitle: "好的")
        alert.runModal()
    }

    @objc private func quitApp() {
        NSApp.terminate(nil)
    }
}

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.setActivationPolicy(.accessory)
app.run()
