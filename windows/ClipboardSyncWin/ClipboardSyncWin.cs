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

        static async Task Main(string[] args)
        {
            StartScan();
            Console.WriteLine("Press Enter to exit.");
            Console.ReadLine();

            if (_device != null) _device.Dispose();
        }

        private static void StartScan()
        {
            _watcher?.Stop();
            _watcher = new BluetoothLEAdvertisementWatcher();
            _watcher.Received += async (w, evt) =>
            {
                if (evt.Advertisement.LocalName == "BLEClipboardSync")
                {
                    _watcher.Stop();
                    Console.WriteLine($"Found: {evt.BluetoothAddress}");
                    await ConnectAndSync(evt.BluetoothAddress);
                }
            };
            _watcher.Start();
        }

        private static async Task ConnectAndSync(ulong address)
        {
            _device?.Dispose();
            _device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
            if (_device == null) return;
            _device.ConnectionStatusChanged += (s, e) =>
            {
                if (_device.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
                {
                    Console.WriteLine("Disconnected. Re-scanning...");
                    StartScan();
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

            Clipboard.ContentChanged += async (s, e) =>
            {
                try
                {
                    var content = Clipboard.GetContent();
                    if (content.Contains(StandardDataFormats.Text))
                    {
                        var text = await content.GetTextAsync();
                        await SendFramesAsync(ProtocolEncoder.EncodeText(text));
                        return;
                    }
                    if (content.Contains(StandardDataFormats.Bitmap))
                    {
                        var bmp = await content.GetBitmapAsync();
                        using (var stream = await bmp.OpenReadAsync())
                        {
                            var bytes = await ReadAllAsync(stream);
                            await SendFramesAsync(ProtocolEncoder.EncodeImage(bytes));
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
                            await SendFramesAsync(ProtocolEncoder.EncodeFile(file.Name, bytes));
                            return;
                        }
                    }
                }
                catch { }
            };
        }

        private static async Task SendFramesAsync(IEnumerable<byte[]> frames)
        {
            foreach (var frame in frames)
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

    static class ProtocolEncoder
    {
        private const int MaxChunkSize = 180;

        public static IEnumerable<byte[]> EncodeText(string text)
        {
            var payload = Encoding.UTF8.GetBytes(text);
            return Encode(0x01, payload);
        }

        public static IEnumerable<byte[]> EncodeImage(byte[] data)
        {
            return Encode(0x02, data);
        }

        public static IEnumerable<byte[]> EncodeFile(string name, byte[] data)
        {
            var nameBytes = Encoding.UTF8.GetBytes(name ?? "clipboard.file");
            var payload = new byte[2 + nameBytes.Length + data.Length];
            payload[0] = (byte)(nameBytes.Length >> 8);
            payload[1] = (byte)(nameBytes.Length & 0xff);
            Buffer.BlockCopy(nameBytes, 0, payload, 2, nameBytes.Length);
            Buffer.BlockCopy(data, 0, payload, 2 + nameBytes.Length, data.Length);
            return Encode(0x03, payload);
        }

        public static IEnumerable<byte[]> Encode(byte type, byte[] payload)
        {
            var total = (ushort)Math.Max(1, (payload.Length + MaxChunkSize - 1) / MaxChunkSize);
            for (int i = 0; i < total; i++)
            {
                int start = i * MaxChunkSize;
                int len = Math.Min(payload.Length - start, MaxChunkSize);
                var data = new byte[8 + len];
                data[0] = type;
                data[1] = (byte)(i == total - 1 ? 0x01 : 0x00);
                data[2] = (byte)((i >> 8) & 0xff);
                data[3] = (byte)(i & 0xff);
                data[4] = (byte)((total >> 8) & 0xff);
                data[5] = (byte)(total & 0xff);
                data[6] = (byte)(len >> 8);
                data[7] = (byte)(len & 0xff);
                Buffer.BlockCopy(payload, start, data, 8, len);
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
            var (type, payload) = result.Value;

            if (type == 0x01)
            {
                var text = Encoding.UTF8.GetString(payload);
                var dp = new DataPackage();
                dp.SetText(text);
                Clipboard.SetContent(dp);
            }
            else if (type == 0x02)
            {
                var stream = new InMemoryRandomAccessStream();
                var writer = new DataWriter(stream);
                writer.WriteBytes(payload);
                await writer.StoreAsync();
                await writer.FlushAsync();
                stream.Seek(0);
                var dp = new DataPackage();
                dp.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
                Clipboard.SetContent(dp);
            }
            else if (type == 0x03)
            {
                if (payload.Length < 2) return;
                int nameLen = (payload[0] << 8) | payload[1];
                if (payload.Length < 2 + nameLen) return;
                var name = Encoding.UTF8.GetString(payload, 2, nameLen);
                var fileData = new byte[payload.Length - 2 - nameLen];
                Buffer.BlockCopy(payload, 2 + nameLen, fileData, 0, fileData.Length);

                var file = await ApplicationData.Current.TemporaryFolder.CreateFileAsync(name, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteBytesAsync(file, fileData);

                var dp = new DataPackage();
                dp.SetStorageItems(new[] { file });
                Clipboard.SetContent(dp);
            }
        }
    }

    class IncomingAssembler
    {
        private byte _currentType = 0;
        private ushort _currentTotal = 0;
        private readonly Dictionary<int, byte[]> _chunks = new Dictionary<int, byte[]>();

        public (byte, byte[])? Append(byte[] frame)
        {
            if (frame.Length < 8) return null;
            var type = frame[0];
            var seq = (frame[2] << 8) | frame[3];
            var total = (ushort)((frame[4] << 8) | frame[5]);
            var len = (frame[6] << 8) | frame[7];
            if (frame.Length < 8 + len) return null;

            if (seq == 0 || type != _currentType || total != _currentTotal)
            {
                _currentType = type;
                _currentTotal = total;
                _chunks.Clear();
            }

            var payload = new byte[len];
            Buffer.BlockCopy(frame, 8, payload, 0, len);
            _chunks[seq] = payload;

            if (_chunks.Count == total)
            {
                var combined = new List<byte>();
                for (int i = 0; i < total; i++)
                {
                    if (_chunks.TryGetValue(i, out var part)) combined.AddRange(part);
                }
                _chunks.Clear();
                return (type, combined.ToArray());
            }
            return null;
        }
    }
}
