using System;
using System.IO;
using System.Text;

namespace VanyaTools.Printing
{
    // Shared by the settings UI and the isolated PDF helper. No colour engine
    // is called: this validates a profile to attach without changing samples.
    internal sealed class IccProfileFile
    {
        internal byte[] Data { get; private set; }
        internal int Channels { get; private set; }
        internal string Model => Channels == 3 ? "RGB" : "CMYK";
        internal string DeviceSpace => Channels == 3 ? "/DeviceRGB" : "/DeviceCMYK";
        internal string DefaultSpace => Channels == 3 ? "/DefaultRGB" : "/DefaultCMYK";

        internal static IccProfileFile Read(string path)
        {
            if (String.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException("Выберите доступный файл ICC/ICM.", path);
            long length = new FileInfo(path).Length;
            if (length < 132 || length > 64 * 1024 * 1024)
                throw new InvalidDataException("Некорректный размер ICC-профиля (допустимо до 64 МБ).");
            byte[] bytes = File.ReadAllBytes(path);
            uint size = UInt32(bytes, 0);
            if (size < 132 || size > bytes.Length || Text(bytes, 36) != "acsp")
                throw new InvalidDataException("Файл не содержит корректный заголовок ICC.");
            if (bytes[8] != 2 && bytes[8] != 4)
                throw new InvalidDataException("Для PDF 1.7 выберите ICC версии 2 или 4.");
            string kind = Text(bytes, 12), pcs = Text(bytes, 20);
            if (kind != "scnr" && kind != "mntr" && kind != "prtr" && kind != "spac")
                throw new InvalidDataException("Нужен обычный входной ICC-профиль. DeviceLink и специальные профили выбираются в RIP.");
            if (pcs != "XYZ " && pcs != "Lab ") throw new InvalidDataException("Неподдерживаемое PCS в ICC-профиле.");
            string model = Text(bytes, 16);
            int channels = model == "RGB " ? 3 : model == "CMYK" ? 4 : 0;
            if (channels == 0) throw new InvalidDataException("Для назначения входного профиля выберите ICC для RGB или CMYK.");
            uint count = UInt32(bytes, 128);
            long tableEnd = 132L + 12L * count;
            if (count == 0 || count > 4096 || tableEnd > size)
                throw new InvalidDataException("Повреждена таблица тегов ICC.");
            for (int i = 0; i < count; i++)
            {
                uint offset = UInt32(bytes, 132 + 12 * i + 4), tagSize = UInt32(bytes, 132 + 12 * i + 8);
                if (offset < tableEnd || (offset & 3) != 0 || tagSize < 8 || (long)offset + tagSize > size)
                    throw new InvalidDataException("Данные тега ICC выходят за границы профиля.");
            }
            if (size != bytes.Length) Array.Resize(ref bytes, (int)size);
            return new IccProfileFile { Data = bytes, Channels = channels };
        }

        private static uint UInt32(byte[] bytes, int offset)
        {
            if (offset < 0 || offset > bytes.Length - 4) throw new InvalidDataException("Обрезанный файл ICC.");
            return ((uint)bytes[offset] << 24) | ((uint)bytes[offset + 1] << 16) |
                ((uint)bytes[offset + 2] << 8) | bytes[offset + 3];
        }
        private static string Text(byte[] bytes, int offset) => Encoding.ASCII.GetString(bytes, offset, 4);
    }
}
