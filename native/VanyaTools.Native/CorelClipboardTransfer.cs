using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace VanyaTools.Native
{
    internal static class CorelClipboardTransfer
    {
        private const uint PmQsSendMessage = 0x00400000; // QS_SENDMESSAGE << 16, PM_NOREMOVE

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeMessage
        {
            public IntPtr Window;
            public uint Message;
            public UIntPtr WParam;
            public IntPtr LParam;
            public uint Time;
            public int PointX, PointY;
            public uint Private;
        }

        [ThreadStatic]
        private static bool _servicingMessages;

        [DllImport("user32.dll", EntryPoint = "PeekMessageW", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PeekMessage(out NativeMessage message, IntPtr window,
            uint filterMin, uint filterMax, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenClipboard(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseClipboard();

        [DllImport("user32.dll")]
        private static extern IntPtr GetOpenClipboardWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        internal static void WaitAvailable(string context, int timeoutMs = 2500)
        {
            var timer = Stopwatch.StartNew();
            do
            {
                // A clipboard reader (including Windows' clipboard service)
                // can hold the lock while sending WM_RENDERFORMAT to Corel.
                // Sleeping on this UI thread prevents Corel from answering.
                ServiceSentMessages();
                // Probe and immediately release the lock. Do not hold it while
                // calling Corel, and do not change the clipboard contents here.
                if (OpenClipboard(IntPtr.Zero))
                {
                    CloseClipboard();
                    if (timer.ElapsedMilliseconds > 0)
                        Log.Info("Sport clipboard: available after " + timer.ElapsedMilliseconds +
                            " ms, " + context);
                    return;
                }
                if (timer.ElapsedMilliseconds >= timeoutMs) break;
                Thread.Sleep(50);
            } while (true);
            string holder = HolderName();
            Log.Info("Sport clipboard: unavailable, " + context + ", holder=" + holder);
            throw new InvalidOperationException("Перенос «" + context +
                "»: буфер обмена занят" + (holder.Length == 0 ? "" : " приложением " + holder) +
                ". Дождитесь его освобождения и повторите этап 1.");
        }

        internal static void Copy(dynamic range, string context)
        {
            for (int attempt = 0; ; attempt++)
            {
                WaitAvailable(context);
                try
                {
                    range.Copy();
                    return;
                }
                catch (COMException error)
                {
                    // Copying leaves the source shapes unchanged and creates
                    // no target shapes, so a bounded retry cannot duplicate art.
                    Log.Error("Sport clipboard: copy failed, " + context + ", attempt=" +
                        (attempt + 1) + ", HRESULT=0x" + error.ErrorCode.ToString("X8"), error);
                    bool transient = error.ErrorCode == unchecked((int)0x800401D0) ||
                        error.ErrorCode == unchecked((int)0x8000FFFF) ||
                        error.ErrorCode == unchecked((int)0x80004005);
                    if (!transient || attempt >= 2)
                        throw new InvalidOperationException("Не удалось скопировать лекало «" + context +
                            "» в буфер обмена (0x" + error.ErrorCode.ToString("X8") + "). " +
                            "Повторите этап 1 после освобождения буфера обмена.", error);
                    WaitForCopyRetry(150 * (attempt + 1));
                }
            }
        }

        private static void ServiceSentMessages()
        {
            if (_servicingMessages) return;
            _servicingMessages = true;
            try
            {
                // PeekMessage dispatches incoming sent messages itself. The
                // PM_QS_SENDMESSAGE flag leaves posted input, timers, paint and
                // dispatcher work queued; there is no general DoEvents loop.
                // PM_NOREMOVE also leaves WM_QUIT for the host message loop.
                PeekMessage(out NativeMessage ignored, IntPtr.Zero, 0, 0, PmQsSendMessage);
            }
            finally { _servicingMessages = false; }
        }

        private static void WaitForCopyRetry(int delayMs)
        {
            var timer = Stopwatch.StartNew();
            do
            {
                ServiceSentMessages();
                int remaining = delayMs - (int)timer.ElapsedMilliseconds;
                if (remaining <= 0) return;
                Thread.Sleep(Math.Min(25, remaining));
            } while (true);
        }

        internal static void ClearModel()
        {
            try
            {
                WaitAvailable("завершение переноса модели", 750);
                System.Windows.Clipboard.Clear();
                Log.Info("Sport overview: model clipboard cleared");
            }
            catch (Exception error) { Log.Error("Could not clear sport model clipboard.", error); }
        }

        private static string HolderName()
        {
            try
            {
                IntPtr window = GetOpenClipboardWindow();
                if (window == IntPtr.Zero) return "";
                GetWindowThreadProcessId(window, out uint processId);
                if (processId == 0) return "";
                using (var process = Process.GetProcessById(checked((int)processId)))
                    return process.ProcessName;
            }
            catch { return ""; }
        }
    }
}
