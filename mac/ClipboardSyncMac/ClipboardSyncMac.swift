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
        // Text only for demo; image/file needs chunking in practice
        if let text = pasteboard.string(forType: .string) {
            let data = ProtocolEncoder.encodeText(text)
            peripheralManager.updateValue(data, for: notifyChar, onSubscribedCentrals: nil)
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
    static func encodeText(_ text: String) -> Data {
        let payload = text.data(using: .utf8) ?? Data()
        return encode(type: 0x01, payload: payload)
    }

    static func encode(type: UInt8, payload: Data) -> Data {
        // Simple single-frame (no chunk). For large payloads, add chunking.
        var data = Data()
        data.append(type)
        data.append(0x01) // flags: last
        data.append(contentsOf: [0x00, 0x00]) // seq
        data.append(contentsOf: [0x00, 0x01]) // total
        let len = UInt16(payload.count)
        data.append(UInt8(len >> 8))
        data.append(UInt8(len & 0xff))
        data.append(payload)
        return data
    }
}

struct ProtocolDecoder {
    static func handleIncoming(data: Data, pasteboard: NSPasteboard) {
        guard data.count >= 8 else { return }
        let type = data[0]
        let len = Int(data[6]) << 8 | Int(data[7])
        guard data.count >= 8 + len else { return }
        let payload = data.subdata(in: 8..<(8+len))
        if type == 0x01, let text = String(data: payload, encoding: .utf8) {
            pasteboard.clearContents()
            pasteboard.setString(text, forType: .string)
        }
    }
}

// MARK: - App Entry
// 在你的 App 入口初始化：
// let _ = ClipboardPeripheral()
