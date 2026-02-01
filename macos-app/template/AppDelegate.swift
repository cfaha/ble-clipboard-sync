import AppKit

final class AppDelegate: NSObject, NSApplicationDelegate {
    private var statusItem: NSStatusItem!
    private var statusMenuItem: NSMenuItem!
    private var trustedMenuItem: NSMenuItem!
    private var trustedMenu: NSMenu!
    private var allowedMenuItem: NSMenuItem!
    private var allowedMenu: NSMenu!
    private var autoStartMenuItem: NSMenuItem!
    private var peripheral: ClipboardPeripheral?
    private let allowedKey = "BLEClipboardAllowedCentralUUIDs"
    private var progressPanel: NSPanel?
    private var progressIndicator: NSProgressIndicator?
    private var progressLabel: NSTextField?
    private var progressCancelButton: NSButton?
    private var isShowingProgress = false
    private var lastStatus: String = ""

    func applicationDidFinishLaunching(_ notification: Notification) {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        statusItem.button?.image = nil
        statusItem.button?.title = "N"

        let menu = NSMenu()
        statusMenuItem = NSMenuItem(title: "状态: 启动中…", action: nil, keyEquivalent: "")
        statusMenuItem.isEnabled = false
        menu.addItem(statusMenuItem)

        trustedMenuItem = NSMenuItem(title: "受信任设备", action: nil, keyEquivalent: "")
        trustedMenu = NSMenu(title: "受信任设备")
        trustedMenuItem.submenu = trustedMenu
        menu.addItem(trustedMenuItem)

        allowedMenuItem = NSMenuItem(title: "允许同步设备", action: nil, keyEquivalent: "")
        allowedMenu = NSMenu(title: "允许同步设备")
        allowedMenuItem.submenu = allowedMenu
        menu.addItem(allowedMenuItem)

        let sendFileItem = NSMenuItem(title: "发送文件…", action: #selector(sendFileManually), keyEquivalent: "")
        menu.addItem(sendFileItem)

        let speedTestMenu = NSMenuItem(title: "测速", action: nil, keyEquivalent: "")
        let speedSub = NSMenu(title: "测速")
        speedSub.addItem(withTitle: "1 MB", action: #selector(speedTest1m), keyEquivalent: "")
        speedSub.addItem(withTitle: "10 MB", action: #selector(speedTest10m), keyEquivalent: "")
        speedSub.addItem(withTitle: "50 MB", action: #selector(speedTest50m), keyEquivalent: "")
        speedSub.addItem(withTitle: "100 MB", action: #selector(speedTest100m), keyEquivalent: "")
        speedSub.addItem(withTitle: "500 MB", action: #selector(speedTest500m), keyEquivalent: "")
        speedTestMenu.submenu = speedSub
        menu.addItem(speedTestMenu)

        autoStartMenuItem = NSMenuItem(title: "开机自启", action: #selector(toggleAutoStart), keyEquivalent: "")
        autoStartMenuItem.state = AutoLaunchManager.isEnabled() ? .on : .off
        menu.addItem(autoStartMenuItem)

        let exportLogItem = NSMenuItem(title: "导出日志", action: #selector(exportLogs), keyEquivalent: "")
        menu.addItem(exportLogItem)

        menu.addItem(NSMenuItem.separator())
        menu.addItem(NSMenuItem(title: "退出", action: #selector(quitApp), keyEquivalent: "q"))
        statusItem.menu = menu

        StatusCenter.shared.onUpdate = { [weak self] state in
            let status = state.rawValue
            self?.lastStatus = status
            if self?.isShowingProgress != true {
                self?.statusMenuItem.title = "状态: \(status)"
            }
            self?.statusItem.button?.toolTip = status
        }
        StatusCenter.shared.set(.disconnected)

        DeviceTrustCenter.shared.onChange = { [weak self] in
            self?.refreshTrustedMenu()
            self?.refreshAllowedMenu()
        }
        refreshTrustedMenu()
        refreshAllowedMenu()

        SpeedTestCenter.shared.onResult = { [weak self] bytes, elapsed, kbps in
            let alert = NSAlert()
            alert.messageText = "测速结果"
            alert.informativeText = String(format: "大小: %.1f KB\n耗时: %.2f s\n速度: %.1f KB/s", Double(bytes)/1024.0, elapsed, kbps)
            alert.addButton(withTitle: "OK")
            alert.runModal()
        }

        peripheral = ClipboardPeripheral()
        peripheral?.onProgress = { [weak self] name, progress, sent, total in
            self?.updateProgress(name: name, progress: progress, sent: sent, total: total)
        }
        peripheral?.onTransferCanceled = { [weak self] in
            self?.isShowingProgress = false
            self?.statusMenuItem.title = "状态: \(self?.lastStatus ?? "")"
            self?.progressPanel?.orderOut(nil)
        }
        LogCenter.shared.log("App started")
    }

    @objc private func exportLogs() {
        do {
            if let url = LogCenter.shared.exportToFile() {
                let path = url.path
                let alert = NSAlert()
                alert.messageText = "日志已导出"
                alert.informativeText = path
                alert.addButton(withTitle: "OK")
                alert.runModal()
                return
            }
            throw NSError(domain: "LogCenter", code: -1)
        } catch {
            let alert = NSAlert()
            alert.messageText = "日志导出失败"
            alert.informativeText = error.localizedDescription
            alert.addButton(withTitle: "OK")
            alert.runModal()
        }
    }

    private func refreshTrustedMenu() {
        trustedMenu.removeAllItems()
        let devices = DeviceTrustCenter.shared.allTrusted()
        if devices.isEmpty {
            trustedMenu.addItem(NSMenuItem(title: "(空)", action: nil, keyEquivalent: ""))
        } else {
            for id in devices {
                let item = NSMenuItem(title: DeviceTrustCenter.shared.displayName(id), action: nil, keyEquivalent: "")
                let sub = NSMenu(title: "操作")
                let rename = NSMenuItem(title: "重命名…", action: #selector(renameTrustedDevice(_:)), keyEquivalent: "")
                rename.representedObject = id
                let remove = NSMenuItem(title: "移除", action: #selector(removeTrustedDevice(_:)), keyEquivalent: "")
                remove.representedObject = id
                sub.addItem(rename)
                sub.addItem(remove)
                item.submenu = sub
                trustedMenu.addItem(item)
            }
            trustedMenu.addItem(NSMenuItem.separator())
            let clear = NSMenuItem(title: "清空", action: #selector(clearTrustedDevices), keyEquivalent: "")
            trustedMenu.addItem(clear)
        }
    }

    private func refreshAllowedMenu() {
        allowedMenu.removeAllItems()
        guard let peripheral = peripheral else {
            allowedMenu.addItem(NSMenuItem(title: "(无连接)", action: nil, keyEquivalent: ""))
            return
        }
        let ids = peripheral.subscribedCentralIds()
        if ids.isEmpty {
            allowedMenu.addItem(NSMenuItem(title: "(无连接)", action: nil, keyEquivalent: ""))
        } else {
            let allowed = loadAllowedSet()
            for id in ids {
                let title = id.uuidString
                let item = NSMenuItem(title: title, action: #selector(toggleAllowedCentral(_:)), keyEquivalent: "")
                item.state = allowed.contains(id) ? .on : .off
                item.representedObject = id
                allowedMenu.addItem(item)
            }
            allowedMenu.addItem(NSMenuItem.separator())
            let clear = NSMenuItem(title: "允许全部", action: #selector(allowAllCentrals), keyEquivalent: "")
            allowedMenu.addItem(clear)
        }
    }

    @objc private func renameTrustedDevice(_ sender: NSMenuItem) {
        guard let id = sender.representedObject as? UInt64 else { return }
        let alert = NSAlert()
        alert.messageText = "重命名设备"
        alert.informativeText = "输入新的别名："
        let input = NSTextField(frame: NSRect(x: 0, y: 0, width: 220, height: 24))
        input.stringValue = DeviceTrustCenter.shared.alias(for: id) ?? ""
        alert.accessoryView = input
        alert.addButton(withTitle: "保存")
        alert.addButton(withTitle: "取消")
        let resp = alert.runModal()
        if resp == .alertFirstButtonReturn {
            DeviceTrustCenter.shared.setAlias(id, alias: input.stringValue)
        }
    }

    @objc private func toggleAllowedCentral(_ sender: NSMenuItem) {
        guard let id = sender.representedObject as? UUID else { return }
        var allowed = loadAllowedSet()
        if allowed.contains(id) { allowed.remove(id) } else { allowed.insert(id) }
        saveAllowedSet(allowed)
        peripheral?.setAllowedCentrals(allowed)
        refreshAllowedMenu()
    }

    @objc private func allowAllCentrals() {
        let empty: Set<UUID> = []
        saveAllowedSet(empty)
        peripheral?.setAllowedCentrals(empty)
        refreshAllowedMenu()
    }

    private func loadAllowedSet() -> Set<UUID> {
        let list = UserDefaults.standard.array(forKey: allowedKey) as? [String] ?? []
        return Set(list.compactMap { UUID(uuidString: $0) })
    }

    private func saveAllowedSet(_ set: Set<UUID>) {
        let list = set.map { $0.uuidString }
        UserDefaults.standard.set(list, forKey: allowedKey)
    }

    @objc private func sendFileManually() {
        guard let peripheral = peripheral else { return }
        let panel = NSOpenPanel()
        panel.canChooseFiles = true
        panel.canChooseDirectories = false
        panel.allowsMultipleSelection = false
        if panel.runModal() == .OK, let url = panel.url {
            showProgressPanel(name: url.lastPathComponent)
            peripheral.sendFile(url)
        }
    }

    private func showProgressPanel(name: String) {
        if progressPanel == nil {
            let panel = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 360, height: 140),
                                styleMask: [.titled, .closable],
                                backing: .buffered,
                                defer: false)
            panel.title = "发送进度"

            let label = NSTextField(labelWithString: "")
            label.frame = NSRect(x: 20, y: 90, width: 320, height: 20)

            let indicator = NSProgressIndicator(frame: NSRect(x: 20, y: 60, width: 320, height: 12))
            indicator.isIndeterminate = false
            indicator.minValue = 0
            indicator.maxValue = 1
            indicator.doubleValue = 0

            let cancel = NSButton(title: "取消", target: self, action: #selector(cancelSend))
            cancel.frame = NSRect(x: 260, y: 20, width: 80, height: 28)

            panel.contentView?.addSubview(label)
            panel.contentView?.addSubview(indicator)
            panel.contentView?.addSubview(cancel)

            progressPanel = panel
            progressIndicator = indicator
            progressLabel = label
            progressCancelButton = cancel
        }

        progressLabel?.stringValue = "\(name)"
        progressIndicator?.doubleValue = 0
        progressPanel?.center()
        progressPanel?.makeKeyAndOrderFront(nil)
        isShowingProgress = true
    }

    private func updateProgress(name: String, progress: Double, sent: Int, total: Int) {
        DispatchQueue.main.async {
            if self.progressPanel == nil { return }
            self.progressLabel?.stringValue = "\(name)  (\(sent)/\(total))"
            self.progressIndicator?.doubleValue = progress
            let percent = Int(progress * 100)
            self.statusMenuItem.title = "发送文件 \(percent)%"
            if progress >= 1.0 {
                self.isShowingProgress = false
                self.statusMenuItem.title = "状态: \(self.lastStatus)"
                self.progressPanel?.orderOut(nil)
            }
        }
    }

    @objc private func cancelSend() {
        peripheral?.cancelCurrentTransfer()
    }

    @objc private func speedTest1m() { peripheral?.startSpeedTest(bytes: 1 * 1024 * 1024) }
    @objc private func speedTest10m() { peripheral?.startSpeedTest(bytes: 10 * 1024 * 1024) }
    @objc private func speedTest50m() { peripheral?.startSpeedTest(bytes: 50 * 1024 * 1024) }
    @objc private func speedTest100m() { peripheral?.startSpeedTest(bytes: 100 * 1024 * 1024) }
    @objc private func speedTest500m() { peripheral?.startSpeedTest(bytes: 500 * 1024 * 1024) }

    @objc private func removeTrustedDevice(_ sender: NSMenuItem) {
        guard let id = sender.representedObject as? UInt64 else { return }
        DeviceTrustCenter.shared.remove(id)
    }

    @objc private func clearTrustedDevices() {
        DeviceTrustCenter.shared.clear()
    }

    @objc private func toggleAutoStart() {
        if AutoLaunchManager.isEnabled() {
            AutoLaunchManager.setEnabled(false)
        } else {
            AutoLaunchManager.setEnabled(true)
        }
        autoStartMenuItem.state = AutoLaunchManager.isEnabled() ? .on : .off
    }

    @objc private func quitApp() {
        NSApp.terminate(nil)
    }
}
