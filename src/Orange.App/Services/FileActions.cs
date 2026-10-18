using System.Diagnostics;
using System.Runtime.InteropServices;
using Orange.App.Interop;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Orange.App.Services;

/// <summary>The Actions wheel (Windows counterparts of Tangerine's AirDrop / Copy / Move / Copy Path / Terminal). They work on folders too.</summary>
public static class FileActions
{
    public sealed record ActionItem(string Id, string Title, string Glyph);

    public static readonly ActionItem[] All =
    [
        new("share", "Share", "\uE72D"),
        new("zip", "Zip", "\uF012"),
        new("copy", "Copy", "\uE8C8"),
        new("move", "Move to…", "\uE8DE"),
        new("copy-path", "Copy path", "\uE71B"),
        new("terminal", "Terminal", "\uE756"),
        new("reveal", "Show in folder", "\uE838"),
    ];

    public static async Task<string> RunAsync(string id, IReadOnlyList<string> paths, IntPtr ownerHwnd)
    {
        switch (id)
        {
            case "zip":
            {
                var zip = await Orange.Core.Providers.ArchiveProvider.CreateZipAsync(paths, null, CancellationToken.None);
                Native.RevealInExplorer([zip]);
                return $"Saved {Path.GetFileName(zip)}";
            }
            case "copy-path":
            {
                var p = new DataPackage();
                p.SetText(string.Join(Environment.NewLine, paths.Select(x => x.Contains(' ') ? $"\"{x}\"" : x)));
                Clipboard.SetContent(p); Clipboard.Flush();
                return paths.Count == 1 ? "Path copied" : $"{paths.Count} paths copied";
            }
            case "copy":
            {
                var items = await Items(paths);
                var p = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
                p.SetStorageItems(items);
                Clipboard.SetContent(p); Clipboard.Flush();
                return $"Copied {Plural(paths.Count, "item")}. Paste in File Explorer.";
            }
            case "move":
            {
                var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Desktop };
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, ownerHwnd);
                var folder = await picker.PickSingleFolderAsync();
                if (folder is null) return "";
                var moved = new List<string>();
                foreach (var src in paths)
                {
                    var dst = Orange.Core.SafePaths.PlanOutput(src, folder.Path, Path.GetFileNameWithoutExtension(src), null,
                        Directory.Exists(src) ? "" : Orange.Core.FileTypeDetector.Ext(src), Orange.Core.OverwritePolicy.KeepBoth, isFolder: Directory.Exists(src));
                    if (Directory.Exists(src)) Directory.Move(src, dst); else File.Move(src, dst);
                    moved.Add(dst);
                }
                Native.RevealInExplorer(moved);
                return $"Moved {Plural(moved.Count, "item")}";
            }
            case "terminal":
            {
                var dir = Directory.Exists(paths[0]) ? paths[0] : Path.GetDirectoryName(paths[0])!;
                // Windows Terminal when present, else PowerShell. The folder is passed as one argument, never through a shell string.
                var psi = new ProcessStartInfo("wt.exe") { UseShellExecute = false };
                psi.ArgumentList.Add("-d"); psi.ArgumentList.Add(dir);
                try { Process.Start(psi); }
                catch
                {
                    var ps = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, WorkingDirectory = dir };
                    ps.ArgumentList.Add("-NoExit");
                    Process.Start(ps);
                }
                return "";
            }
            case "reveal":
                Native.RevealInExplorer(paths);
                return "";
            case "share":
            {
                var items = await Items(paths);
                var dtm = ShareInterop.GetForWindow(ownerHwnd);
                dtm.DataRequested += (s, e) =>
                {
                    e.Request.Data.Properties.Title = paths.Count == 1 ? Path.GetFileName(paths[0]) : $"{paths.Count} files";
                    e.Request.Data.SetStorageItems(items);
                };
                ShareInterop.ShowFor(ownerHwnd);
                return "";
            }
        }
        throw new InvalidOperationException(id);
    }

    private static async Task<List<IStorageItem>> Items(IEnumerable<string> paths)
    {
        var list = new List<IStorageItem>();
        foreach (var p in paths)
            list.Add(Directory.Exists(p) ? await StorageFolder.GetFolderFromPathAsync(p) : await StorageFile.GetFileFromPathAsync(p));
        return list;
    }

    private static string Plural(int n, string w) => n == 1 ? $"1 {w}" : $"{n} {w}s";
}

/// <summary>Desktop apps reach the Windows share sheet through IDataTransferManagerInterop.</summary>
internal static class ShareInterop
{
    [ComImport, Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDataTransferManagerInterop
    {
        IntPtr GetForWindow([In] IntPtr appWindow, [In] ref Guid riid);
        void ShowShareUIForWindow(IntPtr appWindow);
    }
    private static readonly Guid DtmIid = new(0xa5caee9b, 0x8708, 0x49d1, 0x8d, 0x36, 0x67, 0xd2, 0x5a, 0x8d, 0xa0, 0x0c);

    private static IDataTransferManagerInterop Interop() =>
        DataTransferManager.As<IDataTransferManagerInterop>();

    public static DataTransferManager GetForWindow(IntPtr hwnd)
    {
        var iid = DtmIid;
        var ptr = Interop().GetForWindow(hwnd, ref iid);
        return WinRT.MarshalInterface<DataTransferManager>.FromAbi(ptr);
    }
    public static void ShowFor(IntPtr hwnd) => Interop().ShowShareUIForWindow(hwnd);
}
