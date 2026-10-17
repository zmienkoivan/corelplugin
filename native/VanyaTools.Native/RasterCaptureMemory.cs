using System;
using System.Globalization;
using System.Runtime.InteropServices;

namespace VanyaTools.Native
{
    internal static class RasterCaptureMemory
    {
        private const ulong GiB = 1024UL * 1024 * 1024;

        [StructLayout(LayoutKind.Sequential)]
        private sealed class MemoryStatus
        {
            public uint Length;
            public uint Load;
            public ulong TotalPhysical, AvailablePhysical;
            public ulong TotalCommit, AvailableCommit;
            public ulong TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatus status);

        public static void EnsureAvailable(long pixels)
        {
            var state = new MemoryStatus { Length = (uint)Marshal.SizeOf(typeof(MemoryStatus)) };
            if (!GlobalMemoryStatusEx(state))
                throw new InvalidOperationException("Windows не удалось определить доступную память для большого макета. " +
                    "Повторите захват после закрытия лишних приложений.");

            // This is a conservative budget, not a benchmark or a guarantee:
            // native RGB/alpha, managed BGRA, WPF and PNG working buffers.
            ulong working = checked((ulong)pixels * 24 + GiB / 4);
            ulong reserve = Math.Max(GiB, state.TotalPhysical / 32);
            ulong required = checked(working + reserve);
            ulong available = Math.Min(state.AvailablePhysical, state.AvailableCommit);
            Log.Info("Artwork capture memory: pixels=" + pixels + ", required with reserve=" + required +
                ", available physical=" + state.AvailablePhysical + ", available commit=" + state.AvailableCommit);
            if (available < required)
                throw new InvalidOperationException("Для захвата " + (pixels / 1000000.0).ToString("0.#") +
                    " Мп с запасом нужно ориентировочно " + FormatGiB(required) + " ГБ свободной памяти. " +
                    "Сейчас доступно " + FormatGiB(available) + " ГБ. Закройте лишние CDR/приложения " +
                    "или уменьшите выделенный макет.");
        }

        private static string FormatGiB(ulong bytes) =>
            (bytes / (double)GiB).ToString("0.0", CultureInfo.CurrentCulture);
    }
}
