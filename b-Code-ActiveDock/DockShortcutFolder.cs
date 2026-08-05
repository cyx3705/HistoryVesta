using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace ActiveDock;

/// <summary>Keeps the Explorer-facing shortcut folder identical to the current dock list.</summary>
public static class DockShortcutFolder
{
    public const string FolderName = "\u5feb\u6377\u65b9\u5f0f";
    public const string ModuleSlotName = "ActiveDock";

    public static string Path => ResolvePath();

    public static bool IsExplorerRegistrationDisabled
        => string.Equals(
            Environment.GetEnvironmentVariable("ACTIVEDOCK_DISABLE_EXPLORER_REGISTRATION"),
            "1",
            StringComparison.Ordinal);

    /// <summary>
    /// Reconciles only .lnk files owned by the dock. Non-shortcut files are intentionally untouched.
    /// </summary>
    public static ShortcutSyncResult Synchronize(
        IReadOnlyList<DockProject> projects,
        string? folderOverride = null)
    {
        var folder = folderOverride ?? ResolvePath();
        Directory.CreateDirectory(folder);

        var desired = new Dictionary<string, DockProject>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in projects)
        {
            if (!Directory.Exists(project.Path))
                continue;
            desired[LinkPath(folder, project)] = project;
        }

        var removed = 0;
        foreach (var existing in Directory.EnumerateFiles(folder, "*.lnk", SearchOption.TopDirectoryOnly))
        {
            if (desired.ContainsKey(existing))
                continue;
            File.Delete(existing);
            removed++;
        }

        var written = 0;
        foreach (var (linkPath, project) in desired)
        {
            WriteShortcut(linkPath, project);
            written++;
        }

        return new ShortcutSyncResult(folder, written, removed);
    }

    private static string ResolvePath()
    {
        // OHS can shadow-copy a module into a transient load directory. Explorer must
        // always target the durable module slot instead of that transient location.
        return System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OneHistoryStudio",
            "Modules",
            ModuleSlotName,
            FolderName);
    }

    private static string LinkPath(string folder, DockProject project)
    {
        var name = System.IO.Path.GetFileName(project.Path.TrimEnd(
            System.IO.Path.DirectorySeparatorChar,
            System.IO.Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Project path has no directory name.");
        return System.IO.Path.Combine(folder, name + ".lnk");
    }

    private static void WriteShortcut(string linkPath, DockProject project)
    {
        var shellLink = (IShellLinkW)new ShellLink();
        shellLink.SetPath(project.Path);
        shellLink.SetWorkingDirectory(project.Path);
        shellLink.SetDescription(project.Name);
        shellLink.SetIconLocation("%SystemRoot%\\System32\\shell32.dll", 3);
        ((IPersistFile)shellLink).Save(linkPath, true);
    }

    public readonly record struct ShortcutSyncResult(string Folder, int Written, int Removed);

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink
    {
    }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder file, int fileLength, nint findData, uint flags);
        void GetIDList(out nint itemIdList);
        void SetIDList(nint itemIdList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder name, int maxPath);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder directory, int maxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder arguments, int maxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        ushort GetHotkey();
        void SetHotkey(ushort hotkey);
        int GetShowCmd();
        void SetShowCmd(int showCommand);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder iconPath, int iconPathLength, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pathRelative, uint reserved);
        void Resolve(nint hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
