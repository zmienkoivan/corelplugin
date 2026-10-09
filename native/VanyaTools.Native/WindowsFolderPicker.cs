using System;
using System.IO;
using System.Runtime.InteropServices;

namespace VanyaTools.Native
{
    // The .NET Framework FolderBrowserDialog uses the old tree view, which cannot
    // reliably browse network shares. IFileOpenDialog is the Windows folder picker.
    internal static class WindowsFolderPicker
    {
        private const uint PickFolders = 0x20;
        private const uint ForceFileSystem = 0x40;
        private const uint PathMustExist = 0x800;
        private const uint FileSystemPath = 0x80058000;
        private const int Cancelled = unchecked((int)0x800704C7);

        public static string Pick(IntPtr owner, string currentPath)
        {
            IFileOpenDialog dialog = (IFileOpenDialog)new FileOpenDialogCom();
            IShellItem initial = null, result = null;
            try
            {
                dialog.GetOptions(out uint options);
                dialog.SetOptions(options | PickFolders | ForceFileSystem | PathMustExist);
                dialog.SetTitle("Папка года для макетов");
                if (!String.IsNullOrWhiteSpace(currentPath) && Directory.Exists(currentPath))
                {
                    Guid shellItemId = typeof(IShellItem).GUID;
                    int status = SHCreateItemFromParsingName(currentPath, IntPtr.Zero,
                        ref shellItemId, out initial);
                    if (status >= 0 && initial != null) dialog.SetFolder(initial);
                }

                int showStatus = dialog.Show(owner);
                if (showStatus == Cancelled) return null;
                Marshal.ThrowExceptionForHR(showStatus);

                dialog.GetResult(out result);
                if (result == null) throw new InvalidOperationException("Папка не выбрана.");
                result.GetDisplayName(FileSystemPath, out IntPtr pathPointer);
                try { return Marshal.PtrToStringUni(pathPointer); }
                finally { if (pathPointer != IntPtr.Zero) Marshal.FreeCoTaskMem(pathPointer); }
            }
            finally
            {
                if (result != null) Marshal.ReleaseComObject(result);
                if (initial != null) Marshal.ReleaseComObject(initial);
                Marshal.ReleaseComObject(dialog);
            }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext,
            ref Guid shellItemId, out IShellItem item);

        [ComImport, Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
        private class FileOpenDialogCom { }

        [ComImport, Guid("D57C7288-D4AD-4768-BE02-9D969532D960"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOpenDialog
        {
            [PreserveSig] int Show(IntPtr owner);
            void SetFileTypes(uint count, IntPtr filterSpecs);
            void SetFileTypeIndex(uint index);
            void GetFileTypeIndex(out uint index);
            void Advise(IntPtr events, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint options);
            void GetOptions(out uint options);
            void SetDefaultFolder(IShellItem folder);
            void SetFolder(IShellItem folder);
            void GetFolder(out IShellItem folder);
            void GetCurrentSelection(out IShellItem item);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName(out IntPtr name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void GetResult(out IShellItem item);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr bindContext, ref Guid handlerId,
                ref Guid interfaceId, out IntPtr instance);
            void GetParent(out IShellItem parent);
            void GetDisplayName(uint format, out IntPtr name);
        }
    }
}
