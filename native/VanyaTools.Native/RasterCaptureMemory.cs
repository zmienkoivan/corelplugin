using System;
using System.Globalization;
using System.Runtime.InteropServices;

namespace VanyaTools.Native
{
    internal static class RasterCaptureMemory
    {
        private const ulong GiB = 1024UL * 1024 * 1024;

        internal readonly struct Budget
        {
            internal readonly ulong Working, Reserve, Available;
            internal ulong Required => checked(Working + Reserve);
            internal bool Enough => Available >= Required;
            internal Budget(ulong working, ulong reserve, ulong available)
            { Working = working; Reserve = reserve; Available = available; }
        }

        internal static Budget Assess(long pixels, ulong totalPhysical, ulong availablePhysical, ulong availableCommit)
        {
            if (pixels <= 0) throw new ArgumentOutOfRangeException(nameof(pixels), "Размер захвата должен быть больше нуля.");
            // Keep the conservative working-buffer estimate. Extra headroom
            // scales with the job instead of imposing a fixed 1+ GiB floor on
            // tiny stickers. Large jobs retain the original machine reserve.
            ulong working = checked((ulong)pixels * 24 + GiB / 4);
            ulong reserve = Math.Min(Math.Max(GiB, totalPhysical / 32), working);
            return new Budget(working, reserve, Math.Min(availablePhysical, availableCommit));
        }

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
                throw new InvalidOperationException("Windows не удалось определить доступную память для захвата изображения. " +
                    "Повторите захват после закрытия лишних приложений.");

            // This is a conservative budget, not a benchmark or a guarantee:
            // native RGB/alpha, managed BGRA, WPF and PNG working buffers.
            var budget = Assess(pixels, state.TotalPhysical, state.AvailablePhysical, state.AvailableCommit);
            Log.Info("Artwork capture memory: pixels=" + pixels + ", working=" + budget.Working +
                ", reserve=" + budget.Reserve + ", required with reserve=" + budget.Required +
                ", available physical=" + state.AvailablePhysical + ", available commit=" + state.AvailableCommit);
            if (!budget.Enough)
                throw new InvalidOperationException("Для захвата " + (pixels / 1000000.0).ToString("0.#") +
                    " Мп с запасом нужно ориентировочно " + FormatGiB(budget.Required) + " ГБ свободной памяти. " +
                    "Сейчас доступно " + FormatGiB(budget.Available) + " ГБ. Закройте лишние CDR/приложения " +
                    "или уменьшите выделенный макет.");
        }

        private static string FormatGiB(ulong bytes) =>
            (bytes / (double)GiB).ToString("0.0", CultureInfo.CurrentCulture);
    }
}
