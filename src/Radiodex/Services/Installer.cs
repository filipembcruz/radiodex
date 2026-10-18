using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace Radiodex.Services;

/// <summary>
/// Radiodex ships as a single .exe. The first time it runs from anywhere else (Downloads, say),
/// it copies itself to %LocalAppData%\Programs\Radiodex, creates the Desktop and Start menu
/// shortcuts, and restarts from there. Running a newer download later updates that copy.
/// Nothing needs administrator rights.
/// </summary>
public static class Installer
{
    public static string InstallDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Radiodex");

    public static string InstalledExe => Path.Combine(InstallDir, "Radiodex.exe");

    private static string DesktopLink =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Radiodex.lnk");

    private static string StartMenuLink =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Radiodex.lnk");

    /// <summary>
    /// Returns true when this process should exit because the installed copy was started instead.
    /// </summary>
    public static bool InstallAndRelaunchIfNeeded()
    {
        try
        {
            var current = Environment.ProcessPath;
            if (string.IsNullOrEmpty(current)) return false;

            // Only the published single-file exe installs itself (not "dotnet run" during development).
            if (!string.Equals(Path.GetFileName(current), "Radiodex.exe", StringComparison.OrdinalIgnoreCase)) return false;
            // A normal (non single-file) build has Radiodex.dll next to the exe; copying the exe alone would break.
            if (File.Exists(Path.Combine(AppContext.BaseDirectory, "Radiodex.dll"))) return false;

            if (string.Equals(Path.GetFullPath(current), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase))
            {
                EnsureShortcuts();
                return false;
            }

            Directory.CreateDirectory(InstallDir);
            File.Copy(current, InstalledExe, overwrite: true);
            EnsureShortcuts();
            Process.Start(new ProcessStartInfo(InstalledExe) { UseShellExecute = true, WorkingDirectory = InstallDir });
            return true;
        }
        catch
        {
            // Installed copy is running or the folder is locked: just run from here.
            return false;
        }
    }

    /// <summary>Recreates any missing shortcut (they may have been deleted by the user; we don't fight that more than once).</summary>
    public static void EnsureShortcuts()
    {
        var marker = Path.Combine(Storage.Folder, "shortcuts.done");
        try
        {
            if (File.Exists(marker) && File.Exists(StartMenuLink)) return;
            CreateShortcut(StartMenuLink, InstalledExe);
            if (!File.Exists(marker)) CreateShortcut(DesktopLink, InstalledExe);
            Directory.CreateDirectory(Storage.Folder);
            File.WriteAllText(marker, DateTime.UtcNow.ToString("o"));
        }
        catch
        {
            // Shortcuts are a convenience.
        }
    }

    public static void CreateShortcut(string linkPath, string target)
    {
        var link = (IShellLinkW)new ShellLink();
        link.SetPath(target);
        link.SetWorkingDirectory(Path.GetDirectoryName(target) ?? "");
        link.SetIconLocation(target, 0);
        link.SetDescription("Radiodex");
        Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
        ((IPersistFile)link).Save(linkPath, false);
        Marshal.ReleaseComObject(link);
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, int fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, int fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
