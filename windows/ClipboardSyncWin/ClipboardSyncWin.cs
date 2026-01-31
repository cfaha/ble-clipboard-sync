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
                if (LoopState.IgnoreNextChange)
                {
                    LoopState.IgnoreNextChange = false;
                    return;
                }

                try
                {
                    var content = Clipboard.GetContent();
                    if (content.Contains(StandardDataFormats.Text))
                    {
                        var text = await content.GetTextAsync();
                        var payload = Encoding.UTF8.GetBytes(text ?? string.Empty);
                        var hash = CryptoHelper.Sha256(payload);
                        if (LoopState.ShouldSkip(hash)) return;
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

    static class SyncConfig
    {
        public static ulong DeviceId => DeviceIdProvider.GetOrCreate();
        public const string SharedKeyBase64 = "REPLACE_WITH_BASE64_KEY";
        public const int CompressionThreshold = 256;
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
            var content = new byte[body.Length - 8];
            System.Buffer.BlockCopy(body, 8, content, 0, content.Length);
            var hash = CryptoHelper.Sha256(content);

            if (type == 0x01)
            {
                var text = Encoding.UTF8.GetString(content);
                var dp = new DataPackage();
                dp.SetText(text);
                Clipboard.SetContent(dp);
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
                Clipboard.SetContent(dp);
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
                Clipboard.SetContent(dp);
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
