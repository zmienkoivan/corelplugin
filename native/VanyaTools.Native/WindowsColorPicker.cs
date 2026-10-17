using System;
using System.Windows.Media;
using System.Windows.Forms;

namespace VanyaTools.Native
{
    internal static class WindowsColorPicker
    {
        private sealed class DialogOwner : IWin32Window
        {
            public IntPtr Handle { get; }
            public DialogOwner(IntPtr handle) { Handle = handle; }
        }

        private static int[] _customColors;

        public static Color? Pick(IntPtr owner, Color current)
        {
            using (var dialog = new ColorDialog {
                FullOpen = true, AnyColor = true, SolidColorOnly = true,
                Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B) })
            {
                if (_customColors != null) dialog.CustomColors = _customColors;
                DialogResult result = owner == IntPtr.Zero ? dialog.ShowDialog() : dialog.ShowDialog(new DialogOwner(owner));
                _customColors = dialog.CustomColors;
                if (result != DialogResult.OK) return null;
                return Color.FromRgb(dialog.Color.R, dialog.Color.G, dialog.Color.B);
            }
        }
    }
}
