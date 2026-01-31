# BLE Clipboard Sync (Mac ↔ Windows)

蓝牙剪贴板同步工具（Mac + Windows），支持自动同步 **文本 / 图片 / 文件**。
> 注意：无局域网/公网时，**蓝牙是唯一可用链路**，但 BLE 带宽有限，文件/大图需分片传输。

## 设计概览
- **传输方式**：Bluetooth LE (BLE)
- **角色**：
  - Mac 作为 **Peripheral（外设）** 广播服务
  - Windows 作为 **Central（中心设备）** 连接并订阅
- **双向同步**：双方都可写入对方（两条特征：Notify / Write）
- **自动重连**：Windows 断线后自动重新扫描连接

## 传输协议（CBCLP v1）
- 消息帧：`[type:1][flags:1][seq:2][total:2][len:2][payload:len]`
- type：`0x01=text` `0x02=image(png)` `0x03=file`
- flags：`bit0=last` `bit1=compressed`
- seq/total：分片序号
- **file payload**：`[nameLen:2][nameUtf8][fileBytes]`

## 目录结构
```
mac/ClipboardSyncMac/         # macOS (Swift + CoreBluetooth)
windows/ClipboardSyncWin/     # Windows (.NET + Windows.Devices.Bluetooth)
```

## 使用步骤（开发者）
### macOS
1. 打开 `mac/ClipboardSyncMac` 用 Xcode 创建 App（macOS App 或 Menu Bar App）
2. 将 `ClipboardSyncMac.swift` 复制到工程
3. 启用蓝牙权限（Info.plist 添加 `NSBluetoothAlwaysUsageDescription`）
4. 运行，开始广播

### Windows
1. 打开 `windows/ClipboardSyncWin` 用 Visual Studio 创建 WPF/Console 项目
2. 将 `ClipboardSyncWin.cs` 复制到工程
3. 运行，扫描并连接名为 `BLEClipboardSync` 的外设

## 限制
- BLE 带宽有限，大文件会比较慢
- Windows 作为 BLE Peripheral 支持不稳定，因此推荐 **Mac 为外设，Windows 为中心**
- Windows 设置剪贴板文件需要 `StorageFile`，当前实现会先写入临时目录再放入剪贴板
- Windows 设置图片剪贴板需用 `Bitmap`/`IRandomAccessStream` 包装

## 后续可扩展
- 文件压缩/断点续传
- 加密（AES-GCM）
- 设备配对/白名单
- 历史剪贴板

## 若平台 API 有限制的替代方案（计划）
- **文件写入受限**：改为仅发送文件路径提示或生成临时文件并提示用户“点击保存”
- **图片写入失败**：退回发送图片文件并复制其路径（或发送为 data URL 文本）
- **大文件过慢**：增加压缩/差分、可调 chunk size、允许用户设定最大传输大小
