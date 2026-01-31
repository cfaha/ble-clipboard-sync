using System;
using System.Text;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;
using Windows.ApplicationModel.DataTransfer;

namespace ClipboardSyncWin
{
    class Program
    {
        private static readonly Guid ServiceUuid = Guid.Parse("A1B2C3D4-0000-1000-8000-00805F9B34FB");
        private static readonly Guid NotifyUuid  = Guid.Parse("A1B2C3D4-0001-1000-8000-00805F9B34FB");
        private static readonly Guid WriteUuid   = Guid.Parse("A1B2C3D4-0002-1000-8000-00805F9B34FB");

        static async Task Main(string[] args)
        {
            Console.WriteLine("Scanning for BLEClipboardSync...");
            var watcher = new BluetoothLEAdvertisementWatcher();
            watcher.Received += async (w, evt) =>
            {
                if (evt.Advertisement.LocalName == "BLEClipboardSync")
                {
                    watcher.Stop();
                    Console.WriteLine($"Found: {evt.BluetoothAddress}");
                    await ConnectAndSync(evt.BluetoothAddress);
                }
            };
            watcher.Start();

            Console.WriteLine("Press Enter to exit.");
            Console.ReadLine();
        }

        private static async Task ConnectAndSync(ulong address)
        {
            var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
            var services = await device.GetGattServicesForUuidAsync(ServiceUuid);
            if (services.Status != GattCommunicationStatus.Success) return;

            var service = services.Services[0];
            var notifyChars = await service.GetCharacteristicsForUuidAsync(NotifyUuid);
            var writeChars  = await service.GetCharacteristicsForUuidAsync(WriteUuid);
            if (notifyChars.Status != GattCommunicationStatus.Success) return;
            if (writeChars.Status != GattCommunicationStatus.Success) return;

            var notifyChar = notifyChars.Characteristics[0];
            var writeChar  = writeChars.Characteristics[0];

            notifyChar.ValueChanged += (s, e) =>
            {
                var data = new byte[e.CharacteristicValue.Length];
                DataReader.FromBuffer(e.CharacteristicValue).ReadBytes(data);
                ProtocolDecoder.HandleIncoming(data);
            };

            await notifyChar.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify);

            // Monitor clipboard (text only demo)
            var dp = Clipboard.GetContent();
            Clipboard.ContentChanged += async (s, e) =>
            {
                try
                {
                    var content = Clipboard.GetContent();
                    if (content.Contains(StandardDataFormats.Text))
                    {
                        var text = await content.GetTextAsync();
                        var data = ProtocolEncoder.EncodeText(text);
                        var writer = new DataWriter();
                        writer.WriteBytes(data);
                        await writeChar.WriteValueAsync(writer.DetachBuffer());
                    }
                }
                catch { }
            };
        }
    }

    static class ProtocolEncoder
    {
        public static byte[] EncodeText(string text)
        {
            var payload = Encoding.UTF8.GetBytes(text);
            return Encode(0x01, payload);
        }

        public static byte[] Encode(byte type, byte[] payload)
        {
            // single-frame demo
            var len = (ushort)payload.Length;
            var data = new byte[8 + len];
            data[0] = type;
            data[1] = 0x01; // flags: last
            data[2] = 0x00; data[3] = 0x00; // seq
            data[4] = 0x00; data[5] = 0x01; // total
            data[6] = (byte)(len >> 8);
            data[7] = (byte)(len & 0xff);
            Buffer.BlockCopy(payload, 0, data, 8, len);
            return data;
        }
    }

    static class ProtocolDecoder
    {
        public static void HandleIncoming(byte[] data)
        {
            if (data.Length < 8) return;
            var type = data[0];
            var len = (data[6] << 8) | data[7];
            if (data.Length < 8 + len) return;
            var payload = new byte[len];
            Buffer.BlockCopy(data, 8, payload, 0, len);
            if (type == 0x01)
            {
                var text = Encoding.UTF8.GetString(payload);
                var dp = new DataPackage();
                dp.SetText(text);
                Clipboard.SetContent(dp);
            }
        }
    }
}
