using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.ApplicationModel.DataTransfer;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage;
using Windows.Storage.Streams;
using System.Security.Cryptography;
using System.IO;
using System.IO.Compression;
using System.Windows.Forms;
using System.Drawing;
using Microsoft.Win32;

namespace ClipboardSyncWin
{
    class Program
    {
        private static readonly Guid ServiceUuid = Guid.Parse("A1B2C3D4-0000-1000-8000-00805F9B34FB");
        private static readonly Guid NotifyUuid = Guid.Parse("A1B2C3D4-0001-1000-8000-00805F9B34FB");
        private static readonly Guid WriteUuid = Guid.Parse("A1B2C3D4-0002-1000-8000-00805F9B34FB");

        private static BluetoothLEAdvertisementWatcher _watcher;
        private static BluetoothLEDevice _device;
        private static GattCharacteristic _notifyChar;
        private static GattCharacteristic _writeChar;

        private static class WinClipboard
        {
            public static event EventHandler<object> ContentChanged
            {
                add => Windows.ApplicationModel.DataTransfer.Clipboard.ContentChanged += value;
                remove => Windows.ApplicationModel.DataTransfer.Clipboard.ContentChanged -= value;
            }
            public static Windows.ApplicationModel.DataTransfer.DataPackageView GetContent()
                => Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            public static void SetContent(Windows.ApplicationModel.DataTransfer.DataPackage data)
                => Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
        }

        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                LogCenter.Log($"Unhandled exception: {e.ExceptionObject}");
            };
            Application.ThreadException += (s, e) =>
            {
                LogCenter.Log($"Thread exception: {e.Exception}");
            };
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                LogCenter.Log($"Task exception: {e.Exception}");
            };

            LogCenter.Log("App started [v1.0.3-20260201-1638]");
            AppStatus.Initialize();
            _ = StartScanAsync();

            using var context = new TrayAppContext();
            Application.Run(context);

            _watcher?.Stop();
            if (_device != null) _device.Dispose();
        }

        private static async Task StartScanAsync()
        {
            AppStatus.SetConnected(false);
            LogCenter.Log("Start BLE scan");

            try
            {
                var adapter = await BluetoothAdapter.GetDefaultAsync();
                if (adapter == null)
                {
                    LogCenter.Log("No Bluetooth adapter found");
                }
                else
                {
                    LogCenter.Log($"Adapter: LE={adapter.IsLowEnergySupported}, Central={adapter.IsCentralRoleSupported}, Addr={adapter.BluetoothAddress:X}");
                }

                var radios = await Windows.Devices.Radios.Radio.GetRadiosAsync();
                var bt = radios.FirstOrDefault(r => r.Kind == Windows.Devices.Radios.RadioKind.Bluetooth);
                if (bt != null)
                {
                    LogCenter.Log($"Bluetooth radio: {bt.State}");
                }
            }
            catch (Exception ex)
            {
                LogCenter.Log($"Bluetooth init error: {ex.Message}");
            }

            _watcher?.Stop();
            _watcher = new BluetoothLEAdvertisementWatcher
            {
                ScanningMode = BluetoothLEScanningMode.Active
            };
            // StatusChanged not available on this SDK
            _watcher.Received += async (w, evt) =>
            {
                var name = evt.Advertisement.LocalName;
                var hasService = evt.Advertisement.ServiceUuids.Contains(ServiceUuid);
                if (name == "BLEClipboardSync" || hasService)
                {
                    _watcher.Stop();
                    LogCenter.Log($"Found device: {evt.BluetoothAddress:X} name={name} service={hasService}");
                    await ConnectAndSync(evt.BluetoothAddress);
                }
            };
            _watcher.Start();
        }

        private static async Task ConnectAndSync(ulong address)
        {
            LogCenter.Log($"Connecting to {address:X}");
            _device?.Dispose();
            _device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
            if (_device == null)
            {
                LogCenter.Log("Failed to connect: device null");
                return;
            }
            DeviceTrustManager.PromptOnConnect(address);
            _device.ConnectionStatusChanged += (s, e) =>
            {
                if (_device.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
                {
                    AppStatus.SetConnected(false);
                    LogCenter.Log("Disconnected. Re-scanning...");
                    _ = StartScanAsync();
                }
            };

            var services = await _device.GetGattServicesForUuidAsync(ServiceUuid);
            if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0) return;

            var service = services.Services[0];
            var notifyChars = await service.GetCharacteristicsForUuidAsync(NotifyUuid);
            var writeChars = await service.GetCharacteristicsForUuidAsync(WriteUuid);
            if (notifyChars.Status != GattCommunicationStatus.Success || writeChars.Status != GattCommunicationStatus.Success) return;

            _notifyChar = notifyChars.Characteristics[0];
            _writeChar = writeChars.Characteristics[0];

            _notifyChar.ValueChanged += (s, e) =>
            {
                var data = new byte[e.CharacteristicValue.Length];
                DataReader.FromBuffer(e.CharacteristicValue).ReadBytes(data);
                ProtocolDecoder.HandleIncoming(data);
            };

            await _notifyChar.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify);

            AppStatus.SetConnected(true);
            LogCenter.Log("Connected and subscribed to notifications");

            WinClipboard.ContentChanged += async (s, e) =>
            {
                if (LoopState.IgnoreNextChange)
                {
                    LoopState.IgnoreNextChange = false;
                    return;
                }

                try
                {
                    var content = WinClipboard.GetContent();
                    if (content.Contains(StandardDataFormats.Text))
                    {
                        var text = await content.GetTextAsync();
                        var payload = Encoding.UTF8.GetBytes(text ?? string.Empty);
                        var hash = CryptoHelper.Sha256(payload);
                        if (LoopState.ShouldSkip(hash)) return;
                        LogCenter.Log($"Send text: {payload.Length} bytes");
                        await SendFramesAsync(ProtocolEncoder.Encode(0x01, payload));
                        return;
                    }
                    if (content.Contains(StandardDataFormats.Bitmap))
                    {
                        var bmp = await content.GetBitmapAsync();
                        using (var stream = await bmp.OpenReadAsync())
                        {
                            var bytes = await ReadAllAsync(stream);
                            var hash = CryptoHelper.Sha256(bytes);
                            if (LoopState.ShouldSkip(hash)) return;
                            LogCenter.Log($"Send image: {bytes.Length} bytes");
                            await SendFramesAsync(ProtocolEncoder.Encode(0x02, bytes));
                            return;
                        }
                    }
                    if (content.Contains(StandardDataFormats.StorageItems))
                    {
                        var items = await content.GetStorageItemsAsync();
                        var file = items.OfType<StorageFile>().FirstOrDefault();
                        if (file != null)
                        {
                            var buffer = await FileIO.ReadBufferAsync(file);
                            var bytes = buffer.ToArray();
                            var nameBytes = Encoding.UTF8.GetBytes(file.Name ?? "clipboard.file");
                            var payload = new byte[2 + nameBytes.Length + bytes.Length];
                            payload[0] = (byte)(nameBytes.Length >> 8);
                            payload[1] = (byte)(nameBytes.Length & 0xff);
                            System.Buffer.BlockCopy(nameBytes, 0, payload, 2, nameBytes.Length);
                            System.Buffer.BlockCopy(bytes, 0, payload, 2 + nameBytes.Length, bytes.Length);
                            var hash = CryptoHelper.Sha256(payload);
                            if (LoopState.ShouldSkip(hash)) return;
                            LogCenter.Log($"Send file: {payload.Length} bytes, name={file.Name}");
                            await SendFramesAsync(ProtocolEncoder.Encode(0x03, payload));
                            return;
                        }
                    }
                }
                catch { }
            };
        }

        private static async Task SendFramesAsync(IEnumerable<byte[]> frames)
        {
            AppStatus.BumpTransfer();
            var frameList = frames.ToList();
            LogCenter.Log($"Send frames: {frameList.Count}");
            foreach (var frame in frameList)
            {
                var writer = new DataWriter();
                writer.WriteBytes(frame);
                await _writeChar.WriteValueAsync(writer.DetachBuffer(), GattWriteOption.WriteWithoutResponse);
                await Task.Delay(10);
            }
        }

        private static async Task<byte[]> ReadAllAsync(IRandomAccessStream stream)
        {
            var reader = new DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)stream.Size);
            var bytes = new byte[stream.Size];
            reader.ReadBytes(bytes);
            return bytes;
        }
    }

    sealed class TrayAppContext : ApplicationContext
    {
        private readonly NotifyIcon _notifyIcon;
        private readonly ToolStripMenuItem _statusItem;
        private readonly ToolStripMenuItem _trustedMenuItem;
        private readonly ToolStripMenuItem _autoStartItem;
        private readonly SynchronizationContext _syncContext;
        private int _iconRetries = 0;

        public TrayAppContext()
        {
            _syncContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            var menu = new ContextMenuStrip();
            _statusItem = new ToolStripMenuItem("Status: starting…") { Enabled = false };
            _trustedMenuItem = new ToolStripMenuItem("受信任设备");
            _trustedMenuItem.DropDownOpening += (_, __) => RefreshTrustedMenu();
            _autoStartItem = new ToolStripMenuItem("开机自启") { CheckOnClick = true };
            _autoStartItem.Checked = AutoStartManager.IsEnabled();
            _autoStartItem.Click += (_, __) => ToggleAutoStart();
            var exportLogItem = new ToolStripMenuItem("导出日志");
            exportLogItem.Click += (_, __) => ExportLogs();
            var quitItem = new ToolStripMenuItem("Quit");
            quitItem.Click += (_, __) => ExitThread();
            menu.Items.Add(_statusItem);
            menu.Items.Add(_trustedMenuItem);
            menu.Items.Add(_autoStartItem);
            menu.Items.Add(exportLogItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(quitItem);

            _notifyIcon = new NotifyIcon
            {
                Icon = TrayIconFactory.FromState(AppStatus.State.Disconnected),
                Text = "BLE Clipboard Sync",
                ContextMenuStrip = menu,
                Visible = true
            };

            _notifyIcon.Icon = TrayIconFactory.FromState(AppStatus.CurrentState);
            _notifyIcon.Visible = true;
            // retry a few times in case Explorer hasn't loaded tray yet
            for (int i = 1; i <= 3; i++)
            {
                var delay = i * 1000;
                Task.Delay(delay).ContinueWith(_ =>
                {
                    _notifyIcon.Icon = TrayIconFactory.FromState(AppStatus.CurrentState);
                    _notifyIcon.Visible = true;
                });
            }

            DeviceTrustManager.Initialize(_syncContext);
            DeviceTrustManager.OnChanged += RefreshTrustedMenu;
            RefreshTrustedMenu();

            AppStatus.OnStatusChanged += OnStatusChanged;
        }

        private void OnStatusChanged(AppStatus.State state, string status)
        {
            _syncContext.Post(_ =>
            {
                _statusItem.Text = $"状态: {status}";
                var baseText = "BLE Clipboard Sync";
                var combined = $"{baseText} ({status})";
                if (combined.Length > 63)
                {
                    var available = 63 - baseText.Length - 3;
                    combined = available > 0 ? $"{baseText} ({status.Substring(0, available)})" : baseText;
                }
                _notifyIcon.Text = combined;
                _notifyIcon.Icon = TrayIconFactory.FromState(state);
            }, null);
        }

        private void RefreshTrustedMenu()
        {
            void Update()
            {
                _trustedMenuItem.DropDownItems.Clear();
                var devices = DeviceTrustManager.AllTrusted().ToList();
                if (devices.Count == 0)
                {
                    var empty = new ToolStripMenuItem("（暂无）") { Enabled = false };
                    _trustedMenuItem.DropDownItems.Add(empty);
                    return;
                }

                foreach (var id in devices)
                {
                    var deviceItem = new ToolStripMenuItem(DeviceTrustManager.DisplayName(id));
                    var renameItem = new ToolStripMenuItem("重命名…");
                    renameItem.Click += (_, __) => RenameTrustedDevice(id);
                    var removeItem = new ToolStripMenuItem("移除");
                    removeItem.Click += (_, __) => DeviceTrustManager.RemoveTrusted(id);
                    deviceItem.DropDownItems.Add(renameItem);
                    deviceItem.DropDownItems.Add(removeItem);
                    _trustedMenuItem.DropDownItems.Add(deviceItem);
                }
                _trustedMenuItem.DropDownItems.Add(new ToolStripSeparator());
                var clear = new ToolStripMenuItem("清空列表");
                clear.Click += (_, __) => DeviceTrustManager.ClearTrusted();
                _trustedMenuItem.DropDownItems.Add(clear);
            }

            if (SynchronizationContext.Current == _syncContext)
                Update();
            else
                _syncContext.Post(_ => Update(), null);
        }

        private void RenameTrustedDevice(ulong id)
        {
            var current = DeviceTrustManager.GetAlias(id) ?? string.Empty;
            if (!AliasPrompt.TryGetAlias("重命名设备", "设备别名：", current, out var alias)) return;
            DeviceTrustManager.SetAlias(id, alias);
        }

        private void ToggleAutoStart()
        {
            try
            {
                AutoStartManager.SetEnabled(_autoStartItem.Checked);
                _autoStartItem.Checked = AutoStartManager.IsEnabled();
            }
            catch
            {
                _autoStartItem.Checked = AutoStartManager.IsEnabled();
            }
        }

        private void ExportLogs()
        {
            try
            {
                var path = LogCenter.ExportToFile();
                LogCenter.Log($"Export logs to {path}");
                MessageBox.Show($"日志已导出:\n{path}", "导出日志", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"日志导出失败: {ex.Message}", "导出日志", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        protected override void ExitThreadCore()
        {
            AppStatus.OnStatusChanged -= OnStatusChanged;
            DeviceTrustManager.OnChanged -= RefreshTrustedMenu;
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            base.ExitThreadCore();
        }
    }

    static class AliasPrompt
    {
        public static bool TryGetAlias(string title, string label, string initialValue, out string alias)
        {
            alias = initialValue ?? string.Empty;
            using var form = new Form();
            using var textBox = new TextBox();
            using var labelControl = new Label();
            using var buttonOk = new Button();
            using var buttonCancel = new Button();

            form.Text = title;
            form.FormBorderStyle = FormBorderStyle.FixedDialog;
            form.StartPosition = FormStartPosition.CenterScreen;
            form.MinimizeBox = false;
            form.MaximizeBox = false;
            form.ClientSize = new Size(360, 120);
            form.AcceptButton = buttonOk;
            form.CancelButton = buttonCancel;

            labelControl.Text = label;
            labelControl.SetBounds(12, 12, 336, 20);

            textBox.Text = initialValue ?? string.Empty;
            textBox.SetBounds(12, 36, 336, 24);

            buttonOk.Text = "保存";
            buttonOk.DialogResult = DialogResult.OK;
            buttonOk.SetBounds(188, 74, 75, 28);

            buttonCancel.Text = "取消";
            buttonCancel.DialogResult = DialogResult.Cancel;
            buttonCancel.SetBounds(273, 74, 75, 28);

            form.Controls.AddRange(new Control[] { labelControl, textBox, buttonOk, buttonCancel });

            var result = form.ShowDialog();
            if (result != DialogResult.OK) return false;

            alias = textBox.Text;
            return true;
        }
    }

    static class LogCenter
    {
        private static readonly object Locker = new object();
        private static readonly List<string> Buffer = new List<string>();
        private const int MaxLines = 1000;
        private static readonly string LogDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClipboardSyncWin", "logs");
        private static readonly string LiveLogPath = Path.Combine(LogDir, "clipboard-sync-live.log");

        public static void Log(string message)
        {
            var line = $"{DateTime.UtcNow:O} {message}";
            lock (Locker)
            {
                Buffer.Add(line);
                if (Buffer.Count > MaxLines)
                {
                    Buffer.RemoveRange(0, Buffer.Count - MaxLines);
                }
            }
            try { Console.WriteLine(line); } catch { }
            try
            {
                Directory.CreateDirectory(LogDir);
                File.AppendAllText(LiveLogPath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        public static string ExportToFile()
        {
            string[] lines;
            lock (Locker)
            {
                lines = Buffer.ToArray();
            }
            Directory.CreateDirectory(LogDir);
            var filename = $"clipboard-sync-{DateTime.Now:yyyyMMdd-HHmmss}.log";
            var path = Path.Combine(LogDir, filename);
            File.WriteAllLines(path, lines, Encoding.UTF8);
            return path;
        }
    }

    static class AppStatus
    {
        public enum State { Disconnected, Connected, Encrypted, Transferring }

        private static bool _connected;
        private static bool _encrypted;
        private static State _state = State.Disconnected;
        private static State _lastStable = State.Disconnected;
        private static System.Threading.Timer _timer;

        public static event Action<State, string>? OnStatusChanged;
        public static State CurrentState => _state;

        public static void Initialize()
        {
            _encrypted = CryptoHelper.HasKey;
            SetConnected(false);
        }

        public static void SetConnected(bool connected)
        {
            _connected = connected;
            UpdateStable();
        }

        public static void SetEncryptionAvailable(bool encrypted)
        {
            _encrypted = encrypted;
            UpdateStable();
        }

        public static void BumpTransfer()
        {
            _state = State.Transferring;
            Emit();
            _timer?.Dispose();
            _timer = new System.Threading.Timer(_ =>
            {
                _state = _lastStable;
                Emit();
            }, null, 1000, Timeout.Infinite);
        }

        private static void UpdateStable()
        {
            if (!_connected)
                _lastStable = State.Disconnected;
            else
                _lastStable = _encrypted ? State.Encrypted : State.Connected;

            _state = _lastStable;
            Emit();
        }

        private static void Emit()
        {
            var status = _state switch
            {
                State.Disconnected => "未连接",
                State.Connected => "已连接",
                State.Encrypted => "已连接·已加密",
                State.Transferring => "传输中",
                _ => "未知"
            };
            OnStatusChanged?.Invoke(_state, status);
        }
    }

    static class TrayIconFactory
    {
        private static Icon _baseIcon;

        public static Icon FromState(AppStatus.State state)
        {
            var baseIcon = _baseIcon ??= LoadBaseIcon();
            if (baseIcon != null)
                return baseIcon;

            Color color = state switch
            {
                AppStatus.State.Encrypted => Color.FromArgb(34, 197, 94),
                AppStatus.State.Connected => Color.FromArgb(59, 130, 246),
                AppStatus.State.Transferring => Color.FromArgb(245, 158, 11),
                _ => Color.FromArgb(148, 163, 184)
            };
            return MakeDotIcon(color);
        }

        private static Icon LoadBaseIcon()
        {
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                var resName = "ClipboardSyncWin.clipboard-bt.ico";
                using var stream = asm.GetManifestResourceStream(resName);
                if (stream != null) return new Icon(stream, new Size(16, 16));

                return SystemIcons.Application;
            }
            catch { return null; }
        }

        private static Icon MakeDotIcon(Color color)
        {
            using var bmp = new Bitmap(16, 16);
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 2, 2, 12, 12);
            var hIcon = bmp.GetHicon();
            return Icon.FromHandle(hIcon);
        }
    }

    static class SyncConfig
    {
        public static ulong DeviceId => DeviceIdProvider.GetOrCreate();
        public const string SharedKeyBase64 = "tKl/HZaBOndm38qZMtArGBgQa1ZuL26QER+jksZp9NY=";
        public const int CompressionThreshold = 256;
    }

    static class AutoStartManager
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "BLEClipboardSync";

        public static bool IsEnabled()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            if (key == null) return false;
            var value = key.GetValue(ValueName) as string;
            if (string.IsNullOrWhiteSpace(value)) return false;
            var exe = Application.ExecutablePath;
            return value.IndexOf(exe, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static void SetEnabled(bool enabled)
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (key == null) return;
            if (enabled)
            {
                key.SetValue(ValueName, $"\"{Application.ExecutablePath}\"");
            }
            else
            {
                key.DeleteValue(ValueName, false);
            }
        }
    }

    static class DeviceIdProvider
    {
        private static ulong? _cached;
        public static ulong GetOrCreate()
        {
            if (_cached.HasValue) return _cached.Value;
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClipboardSyncWin");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "device.id");
                if (File.Exists(path))
                {
                    var bytes = File.ReadAllBytes(path);
                    if (bytes.Length == 8)
                    {
                        _cached = BitConverter.ToUInt64(bytes, 0);
                        return _cached.Value;
                    }
                }
                var rnd = RandomNumberGenerator.GetBytes(8);
                File.WriteAllBytes(path, rnd);
                _cached = BitConverter.ToUInt64(rnd, 0);
                return _cached.Value;
            }
            catch
            {
                // fallback: hash machine name
                using var sha = SHA256.Create();
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(Environment.MachineName));
                _cached = BitConverter.ToUInt64(hash, 0);
                return _cached.Value;
            }
        }
    }

    class DeviceEntry
    {
        public string Id { get; set; }
        public string Alias { get; set; }
    }

    static class DeviceTrustStore
    {
        private static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClipboardSyncWin");
        private static readonly string PathFile = System.IO.Path.Combine(Dir, "trusted.json");
        private static readonly Dictionary<ulong, string> Trusted = Load();

        public static IEnumerable<ulong> All() => Trusted.Keys.OrderBy(x => x);

        public static bool IsTrusted(ulong id) => Trusted.ContainsKey(id);

        public static string GetAlias(ulong id) => Trusted.TryGetValue(id, out var alias) ? alias : null;

        public static void Add(ulong id)
        {
            if (!Trusted.ContainsKey(id))
            {
                Trusted[id] = null;
                Save();
            }
        }

        public static void SetAlias(ulong id, string alias)
        {
            if (!Trusted.ContainsKey(id)) Trusted[id] = null;
            var trimmed = string.IsNullOrWhiteSpace(alias) ? null : alias.Trim();
            if (!Trusted.TryGetValue(id, out var current) || current != trimmed)
            {
                Trusted[id] = trimmed;
                Save();
            }
        }

        public static void Remove(ulong id)
        {
            if (Trusted.Remove(id)) Save();
        }

        public static void Clear()
        {
            if (Trusted.Count == 0) return;
            Trusted.Clear();
            Save();
        }

        private static Dictionary<ulong, string> Load()
        {
            try
            {
                if (!File.Exists(PathFile)) return new Dictionary<ulong, string>();
                var json = File.ReadAllText(PathFile);
                try
                {
                    var entries = System.Text.Json.JsonSerializer.Deserialize<List<DeviceEntry>>(json);
                    if (entries != null && entries.Count > 0)
                    {
                        var dict = new Dictionary<ulong, string>();
                        foreach (var entry in entries)
                        {
                            if (entry == null || string.IsNullOrWhiteSpace(entry.Id)) continue;
                            if (ulong.TryParse(entry.Id, out var id) && id != 0)
                            {
                                var alias = string.IsNullOrWhiteSpace(entry.Alias) ? null : entry.Alias.Trim();
                                dict[id] = alias;
                            }
                        }
                        return dict;
                    }
                }
                catch { }

                try
                {
                    var list = System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
                    var dict = new Dictionary<ulong, string>();
                    foreach (var item in list)
                    {
                        if (ulong.TryParse(item, out var id) && id != 0)
                        {
                            dict[id] = null;
                        }
                    }
                    return dict;
                }
                catch { }

                return new Dictionary<ulong, string>();
            }
            catch
            {
                return new Dictionary<ulong, string>();
            }
        }

        private static void Save()
        {
            Directory.CreateDirectory(Dir);
            var list = Trusted.OrderBy(x => x.Key).Select(x => new DeviceEntry
            {
                Id = x.Key.ToString(),
                Alias = string.IsNullOrWhiteSpace(x.Value) ? null : x.Value
            }).ToList();
            var json = System.Text.Json.JsonSerializer.Serialize(list, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(PathFile, json);
            DeviceTrustManager.NotifyChanged();
        }
    }

    static class DeviceTrustManager
    {
        private static SynchronizationContext _syncContext;
        private static bool _allowNextUnknown = false;
        public static event Action? OnChanged;

        public static void Initialize(SynchronizationContext context)
        {
            _syncContext = context;
        }

        public static IEnumerable<ulong> AllTrusted() => DeviceTrustStore.All();

        public static string GetAlias(ulong id) => DeviceTrustStore.GetAlias(id);

        public static void SetAlias(ulong id, string alias) => DeviceTrustStore.SetAlias(id, alias);

        public static string DisplayName(ulong id)
        {
            var alias = DeviceTrustStore.GetAlias(id);
            if (!string.IsNullOrWhiteSpace(alias)) return $"{alias} ({Format(id)})";
            return Format(id);
        }

        public static bool EnsureTrusted(ulong id)
        {
            if (DeviceTrustStore.IsTrusted(id)) return true;
            if (_allowNextUnknown)
            {
                _allowNextUnknown = false;
                DeviceTrustStore.Add(id);
                return true;
            }
            return PromptTrust(id);
        }

        public static void PromptOnConnect(ulong address)
        {
            bool allowed = false;
            using var wait = new ManualResetEventSlim(false);
            void Prompt()
            {
                var msg = $"检测到新连接: {address:X}\n是否允许后续剪贴板同步？";
                var result = MessageBox.Show(msg, "允许此设备连接？", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                allowed = (result == DialogResult.Yes);
                wait.Set();
            }

            if (_syncContext != null)
                _syncContext.Post(_ => Prompt(), null);
            else
                Prompt();

            wait.Wait();
            _allowNextUnknown = allowed;
        }

        public static void RemoveTrusted(ulong id) => DeviceTrustStore.Remove(id);
        public static void ClearTrusted() => DeviceTrustStore.Clear();

        private static bool PromptTrust(ulong id)
        {
            bool allowed = false;
            using var wait = new ManualResetEventSlim(false);
            void Prompt()
            {
                var msg = $"检测到新的设备: {DisplayName(id)}\n是否允许与其同步剪贴板？";
                var result = MessageBox.Show(msg, "信任此设备？", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (result == DialogResult.Yes)
                {
                    DeviceTrustStore.Add(id);
                    allowed = true;
                }
                wait.Set();
            }

            if (_syncContext != null)
                _syncContext.Post(_ => Prompt(), null);
            else
                Prompt();

            wait.Wait();
            return allowed;
        }

        public static void NotifyChanged()
        {
            OnChanged?.Invoke();
        }

        public static string Format(ulong id) => id.ToString("X16");
    }

    static class LoopState
    {
        public static bool IgnoreNextChange = false;
        public static byte[] LastReceivedHash = null;

        public static bool ShouldSkip(byte[] hash)
        {
            if (LastReceivedHash == null) return false;
            return LastReceivedHash.SequenceEqual(hash);
        }

        public static void MarkReceived(byte[] hash)
        {
            LastReceivedHash = hash;
            IgnoreNextChange = true;
        }
    }

    static class CryptoHelper
    {
        public static byte[] Sha256(byte[] data)
        {
            using var sha = SHA256.Create();
            return sha.ComputeHash(data);
        }

        public static bool HasKey
        {
            get
            {
                return TryGetKey(out _);
            }
        }

        private static bool TryGetKey(out byte[] key)
        {
            key = null;
            try
            {
                var k = Convert.FromBase64String(SyncConfig.SharedKeyBase64 ?? string.Empty);
                if (k.Length == 16 || k.Length == 24 || k.Length == 32)
                {
                    key = k;
                    return true;
                }
            }
            catch { }
            return false;
        }

        public static byte[] Encrypt(byte[] data, byte[] aad)
        {
            if (!TryGetKey(out var key)) return null;
            var nonce = RandomNumberGenerator.GetBytes(12);
            var cipher = new byte[data.Length];
            var tag = new byte[16];
            using var aes = new AesGcm(key);
            aes.Encrypt(nonce, data, cipher, tag, aad);
            var outBuf = new byte[nonce.Length + cipher.Length + tag.Length];
            System.Buffer.BlockCopy(nonce, 0, outBuf, 0, nonce.Length);
            System.Buffer.BlockCopy(cipher, 0, outBuf, nonce.Length, cipher.Length);
            System.Buffer.BlockCopy(tag, 0, outBuf, nonce.Length + cipher.Length, tag.Length);
            return outBuf;
        }

        public static byte[] Decrypt(byte[] data, byte[] aad)
        {
            if (!TryGetKey(out var key)) return null;
            if (data.Length < 12 + 16) return null;
            var nonce = new byte[12];
            var tag = new byte[16];
            var cipher = new byte[data.Length - 12 - 16];
            System.Buffer.BlockCopy(data, 0, nonce, 0, 12);
            System.Buffer.BlockCopy(data, 12, cipher, 0, cipher.Length);
            System.Buffer.BlockCopy(data, 12 + cipher.Length, tag, 0, 16);
            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(key);
            try
            {
                aes.Decrypt(nonce, cipher, tag, plain, aad);
                return plain;
            }
            catch
            {
                return null;
            }
        }
    }

    static class CompressionHelper
    {
        public static byte[] Compress(byte[] data)
        {
            if (data == null || data.Length == 0) return new byte[] { 0, 0, 0, 0 };
            using var ms = new MemoryStream();
            ms.WriteByte((byte)(data.Length >> 24));
            ms.WriteByte((byte)((data.Length >> 16) & 0xff));
            ms.WriteByte((byte)((data.Length >> 8) & 0xff));
            ms.WriteByte((byte)(data.Length & 0xff));
            using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, true))
            {
                ds.Write(data, 0, data.Length);
            }
            return ms.ToArray();
        }

        public static byte[] Decompress(byte[] data)
        {
            if (data == null || data.Length < 4) return null;
            int expected = (data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3];
            using var src = new MemoryStream(data, 4, data.Length - 4);
            using var ds = new DeflateStream(src, CompressionMode.Decompress);
            using var outMs = new MemoryStream();
            ds.CopyTo(outMs);
            var outBuf = outMs.ToArray();
            if (expected > 0 && outBuf.Length != expected)
            {
                // ignore length mismatch
            }
            return outBuf;
        }
    }

    static class ProtocolEncoder
    {
        private const int MaxChunkSize = 180;

        public static IEnumerable<byte[]> Encode(byte type, byte[] payload)
        {
            byte flags = 0;
            var body = new List<byte>();
            body.Add((byte)(SyncConfig.DeviceId >> 56));
            body.Add((byte)((SyncConfig.DeviceId >> 48) & 0xff));
            body.Add((byte)((SyncConfig.DeviceId >> 40) & 0xff));
            body.Add((byte)((SyncConfig.DeviceId >> 32) & 0xff));
            body.Add((byte)((SyncConfig.DeviceId >> 24) & 0xff));
            body.Add((byte)((SyncConfig.DeviceId >> 16) & 0xff));
            body.Add((byte)((SyncConfig.DeviceId >> 8) & 0xff));
            body.Add((byte)(SyncConfig.DeviceId & 0xff));
            body.AddRange(payload ?? Array.Empty<byte>());

            if (body.Count >= SyncConfig.CompressionThreshold)
            {
                var compressed = CompressionHelper.Compress(body.ToArray());
                if (compressed != null)
                {
                    body = new List<byte>(compressed);
                    flags |= 0x02;
                }
            }

            var encrypted = CryptoHelper.Encrypt(body.ToArray(), new byte[] { type, (byte)(flags | 0x04) });
            if (encrypted != null)
            {
                body = new List<byte>(encrypted);
                flags |= 0x04;
            }

            var total = (ushort)Math.Max(1, (body.Count + MaxChunkSize - 1) / MaxChunkSize);
            for (int i = 0; i < total; i++)
            {
                int start = i * MaxChunkSize;
                int len = Math.Min(body.Count - start, MaxChunkSize);
                var data = new byte[8 + len];
                data[0] = type;
                data[1] = (byte)((i == total - 1 ? 0x01 : 0x00) | flags);
                data[2] = (byte)((i >> 8) & 0xff);
                data[3] = (byte)(i & 0xff);
                data[4] = (byte)((total >> 8) & 0xff);
                data[5] = (byte)(total & 0xff);
                data[6] = (byte)(len >> 8);
                data[7] = (byte)(len & 0xff);
                body.CopyTo(start, data, 8, len);
                yield return data;
            }
        }
    }

    static class ProtocolDecoder
    {
        private static readonly IncomingAssembler Assembler = new IncomingAssembler();

        public static async void HandleIncoming(byte[] data)
        {
            var result = Assembler.Append(data);
            if (result == null) return;
            var (type, flags, payload) = result.Value;

            var body = payload;
            if ((flags & 0x04) != 0)
            {
                var decrypted = CryptoHelper.Decrypt(body, new byte[] { type, flags });
                if (decrypted == null) return;
                body = decrypted;
            }

            if ((flags & 0x02) != 0)
            {
                var decompressed = CompressionHelper.Decompress(body);
                if (decompressed == null) return;
                body = decompressed;
            }

            if (body.Length < 4) return;
            ulong senderId = ((ulong)body[0] << 56) | ((ulong)body[1] << 48) | ((ulong)body[2] << 40) | ((ulong)body[3] << 32) |
                             ((ulong)body[4] << 24) | ((ulong)body[5] << 16) | ((ulong)body[6] << 8) | (ulong)body[7];
            if (senderId == SyncConfig.DeviceId) return;
            if (!DeviceTrustManager.EnsureTrusted(senderId)) return;
            var content = new byte[body.Length - 8];
            System.Buffer.BlockCopy(body, 8, content, 0, content.Length);
            var hash = CryptoHelper.Sha256(content);
            AppStatus.BumpTransfer();
            LogCenter.Log($"Received type={type}, bytes={content.Length}");

            if (type == 0x01)
            {
                var text = Encoding.UTF8.GetString(content);
                var dp = new DataPackage();
                dp.SetText(text);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
                LoopState.MarkReceived(hash);
            }
            else if (type == 0x02)
            {
                var stream = new InMemoryRandomAccessStream();
                var writer = new DataWriter(stream);
                writer.WriteBytes(content);
                await writer.StoreAsync();
                await writer.FlushAsync();
                stream.Seek(0);
                var dp = new DataPackage();
                dp.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
                LoopState.MarkReceived(hash);
            }
            else if (type == 0x03)
            {
                if (content.Length < 2) return;
                int nameLen = (content[0] << 8) | content[1];
                if (content.Length < 2 + nameLen) return;
                var name = Encoding.UTF8.GetString(content, 2, nameLen);
                var fileData = new byte[content.Length - 2 - nameLen];
                System.Buffer.BlockCopy(content, 2 + nameLen, fileData, 0, fileData.Length);

                var file = await ApplicationData.Current.TemporaryFolder.CreateFileAsync(name, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteBytesAsync(file, fileData);

                var dp = new DataPackage();
                dp.SetStorageItems(new[] { file });
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
                LoopState.MarkReceived(hash);
            }
        }
    }

    class IncomingAssembler
    {
        private byte _currentType = 0;
        private ushort _currentTotal = 0;
        private byte _currentFlags = 0;
        private readonly Dictionary<int, byte[]> _chunks = new Dictionary<int, byte[]>();

        public (byte, byte, byte[])? Append(byte[] frame)
        {
            if (frame.Length < 8) return null;
            var type = frame[0];
            var flags = (byte)(frame[1] & 0x06);
            var seq = (frame[2] << 8) | frame[3];
            var total = (ushort)((frame[4] << 8) | frame[5]);
            var len = (frame[6] << 8) | frame[7];
            if (frame.Length < 8 + len) return null;

            if (seq == 0 || type != _currentType || total != _currentTotal)
            {
                _currentType = type;
                _currentTotal = total;
                _currentFlags = flags;
                _chunks.Clear();
            }

            var payload = new byte[len];
            System.Buffer.BlockCopy(frame, 8, payload, 0, len);
            _chunks[seq] = payload;

            if (_chunks.Count == total)
            {
                var combined = new List<byte>();
                for (int i = 0; i < total; i++)
                {
                    if (_chunks.TryGetValue(i, out var part)) combined.AddRange(part);
                }
                _chunks.Clear();
                return (_currentType, _currentFlags, combined.ToArray());
            }
            return null;
        }
    }
}
