using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Dlss5Suite.Setup.Services;

/// <summary>
/// Writes a .lnk through the shell's own COM interfaces.
///
/// The Windows Script Host object was doing this before and was simpler, but
/// it cannot set an AppUserModelID on the shortcut - and without one Windows
/// has no name to identify the application by, so the taskbar pin request has
/// nothing to pin and the app gets its own separate taskbar button instead of
/// grouping with its shortcut. That property is only reachable through
/// IPropertyStore, so the shortcut is built the long way.
/// </summary>
internal static class ShellLink
{
    // The AUMID. Windows uses it to tie the running process, the Start menu
    // entry and the taskbar button together as one application.
    public const string AppUserModelId = "PotatoesDev.DLSS5Suite";

    public static void Create(string linkPath, string targetPath, string description)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);

        var link = (IShellLinkW)new CShellLink();

        try
        {
            link.SetPath(targetPath);
            link.SetWorkingDirectory(Path.GetDirectoryName(targetPath) ?? "");
            link.SetIconLocation(targetPath, 0);
            link.SetDescription(description);

            // The property that makes the pin possible.
            var store = (IPropertyStore)link;
            var key = PropertyKeys.AppUserModelId;

            var value = new PropVariant();

            try
            {
                value.Init(AppUserModelId);
                store.SetValue(ref key, ref value);
                store.Commit();
            }
            finally
            {
                value.Clear();
            }

            ((IPersistFile)link).Save(linkPath, true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    /// <summary>
    /// Tells Windows which application this process belongs to. Must be called
    /// before any window is shown, and must match the shortcut's ID exactly.
    /// </summary>
    public static void TagCurrentProcess()
    {
        try { SetCurrentProcessExplicitAppUserModelID(AppUserModelId); } catch { }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] string appId);

    // -----------------------------------------------------------------
    // COM
    // -----------------------------------------------------------------
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink { }

    [ComImport,
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
     Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder file,
                     int maxPath, IntPtr findData, uint flags);
        void GetIDList(out IntPtr pidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder name, int maxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder dir, int maxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder args, int maxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder icon,
                             int maxPath, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string icon, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relative, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport,
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
     Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName,
                  [MarshalAs(UnmanagedType.Bool)] bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }

    [ComImport,
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
     Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public int PropertyId;

        public PropertyKey(Guid formatId, int propertyId)
        {
            FormatId = formatId;
            PropertyId = propertyId;
        }
    }

    private static class PropertyKeys
    {
        /// System.AppUserModel.ID
        public static PropertyKey AppUserModelId =
            new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)] private ushort _type;
        [FieldOffset(8)] private IntPtr _pointer;

        private const ushort VT_LPWSTR = 31;

        public void Init(string value)
        {
            Clear();
            _type = VT_LPWSTR;
            _pointer = Marshal.StringToCoTaskMemUni(value);
        }

        public void Clear()
        {
            if (_pointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(_pointer);
                _pointer = IntPtr.Zero;
            }

            _type = 0;
        }
    }
}
