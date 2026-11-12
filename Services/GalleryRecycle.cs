using System;
using System.IO;
using System.Runtime.InteropServices;

namespace GraphiteCompanion.Services;

public static class GalleryRecycle
{
    // Run on the UI STA. The shell is explicitly instructed to recycle, never to fall back to File.Delete.
    public static void Delete(string root, GalleryItem item)
    {
        string path = Path.GetFullPath(item.Path);
        string basePath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(basePath, StringComparison.OrdinalIgnoreCase) || !GalleryService.IsMediaFile(path))
            throw new IOException("Файл находится вне папки галереи.");
        for (string? part = path; part != null && part.Length >= basePath.Length - 1; part = Path.GetDirectoryName(part))
            if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0) throw new IOException("Удаление через ссылки не поддерживается.");
        var info = new FileInfo(path);
        if (info.Length != item.Bytes || info.LastWriteTimeUtc != item.Modified)
            throw new IOException("Файл изменился. Обновите галерею и повторите удаление.");
        IShellItem? shellItem = null;
        var operation = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("3ad05575-8857-4850-9277-11b85bdb8e09"))!)!;
        try
        {
            var iid = new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe");
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out shellItem));
            operation.SetOperationFlags(0x00080000 | 0x00100000 | 0x20000000 | 0x0400 | 0x0010 | 0x0004);
            operation.DeleteItem(shellItem, IntPtr.Zero);
            operation.PerformOperations();
            operation.GetAnyOperationsAborted(out bool aborted);
            if (aborted || File.Exists(path)) throw new IOException("Не удалось переместить файл в корзину. Возможно, файл занят или корзина недоступна.");
        }
        finally
        {
            if (shellItem != null) Marshal.FinalReleaseComObject(shellItem);
            Marshal.FinalReleaseComObject(operation);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bind, ref Guid iid, out IShellItem item);

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr context, ref Guid handler, ref Guid iid, out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint kind, out IntPtr name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }

    [ComImport, Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        void Advise(IntPtr sink, out uint cookie);
        void Unadvise(uint cookie);
        void SetOperationFlags(uint flags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        void SetProgressDialog(IntPtr dialog);
        void SetProperties(IntPtr properties);
        void SetOwnerWindow(IntPtr owner);
        void ApplyPropertiesToItem(IntPtr item);
        void ApplyPropertiesToItems(IntPtr items);
        void RenameItem(IntPtr item, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        void RenameItems(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string name);
        void MoveItem(IntPtr item, IntPtr folder, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        void MoveItems(IntPtr items, IntPtr folder);
        void CopyItem(IntPtr item, IntPtr folder, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        void CopyItems(IntPtr items, IntPtr folder);
        void DeleteItem(IShellItem item, IntPtr sink);
        void DeleteItems(IntPtr items);
        void NewItem(IntPtr folder, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, IntPtr sink);
        void PerformOperations();
        void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
}
