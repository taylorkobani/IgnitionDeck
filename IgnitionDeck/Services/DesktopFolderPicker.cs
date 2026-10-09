using System.Runtime.InteropServices;
using IgnitionDeck.Core;

namespace IgnitionDeck.Services;

/// <summary>Desktop shell folder selection, including elevated desktop processes.</summary>
internal static class DesktopFolderPicker
{
    private const int Cancelled = unchecked((int)0x800704C7);
    private const uint PickFolders = 0x20;
    private const uint ForceFileSystem = 0x40;
    private const uint PathMustExist = 0x800;
    private const uint FileSystemPath = 0x80058000;

    public static Task<string?> PickAsync(nint owner)
    {
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.SetResult(Pick(owner)); }
            catch (Exception exception) { completion.SetException(exception); }
        }) { IsBackground = true, Name = "IgnitionDeck folder picker" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    public static string? Pick(nint owner)
    {
        if (owner == 0) throw new InvalidOperationException("Folder selection requires an active desktop window.");
        return OperationDiagnostics.Execute("Open desktop folder selection dialog", () =>
        {
            var type = Type.GetTypeFromCLSID(new Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7"), throwOnError: true)!;
            object dialogObject = Activator.CreateInstance(type)!;
            IShellItem? item = null;
            try
            {
                var dialog = (IFileDialog)dialogObject;
                Marshal.ThrowExceptionForHR(dialog.GetOptions(out var options));
                Marshal.ThrowExceptionForHR(dialog.SetOptions(options | PickFolders | ForceFileSystem | PathMustExist));
                Marshal.ThrowExceptionForHR(dialog.SetTitle("Select folder"));
                var result = dialog.Show(owner);
                if (result == Cancelled) return null;
                Marshal.ThrowExceptionForHR(result);
                Marshal.ThrowExceptionForHR(dialog.GetResult(out item));
                Marshal.ThrowExceptionForHR(item.GetDisplayName(FileSystemPath, out var path));
                try { return Marshal.PtrToStringUni(path); }
                finally { Marshal.FreeCoTaskMem(path); }
            }
            finally
            {
                if (item is not null) Marshal.FinalReleaseComObject(item);
                Marshal.FinalReleaseComObject(dialogObject);
            }
        });
    }

    // Methods remain in native vtable order, including unused slots before GetResult.
    [ComImport, Guid("42F85136-DB7E-439C-85F1-E4075D135FC8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        [PreserveSig] int Show(nint owner);
        [PreserveSig] int SetFileTypes(uint count, nint filters);
        [PreserveSig] int SetFileTypeIndex(uint index);
        [PreserveSig] int GetFileTypeIndex(out uint index);
        [PreserveSig] int Advise(nint events, out uint cookie);
        [PreserveSig] int Unadvise(uint cookie);
        [PreserveSig] int SetOptions(uint options);
        [PreserveSig] int GetOptions(out uint options);
        [PreserveSig] int SetDefaultFolder(IShellItem folder);
        [PreserveSig] int SetFolder(IShellItem folder);
        [PreserveSig] int GetFolder(out IShellItem folder);
        [PreserveSig] int GetCurrentSelection(out IShellItem item);
        [PreserveSig] int SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int GetFileName(out nint name);
        [PreserveSig] int SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        [PreserveSig] int SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        [PreserveSig] int SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        [PreserveSig] int GetResult(out IShellItem item);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(nint context, in Guid handler, in Guid iid, out nint result);
        [PreserveSig] int GetParent(out IShellItem parent);
        [PreserveSig] int GetDisplayName(uint kind, out nint name);
        [PreserveSig] int GetAttributes(uint mask, out uint attributes);
        [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
    }
}
