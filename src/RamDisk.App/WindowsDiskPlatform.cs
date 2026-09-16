using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using RamDisk.Core;
using Microsoft.Win32;

namespace RamDisk.App;

public sealed class WindowsDiskPlatform : IDiskPlatform
{
    // Do not resolve an elevated executable from PATH or from user-supplied settings.
    public string DriverExecutable => Path.Combine(Environment.SystemDirectory, "imdisk.exe");
    public bool IsDriverInstalled => File.Exists(DriverExecutable) && File.Exists(Path.Combine(Environment.SystemDirectory, "drivers", "imdisk.sys"));
    public bool IsPhysicalMemoryDriverInstalled
    {
        get
        {
            if (!File.Exists(Path.Combine(Environment.SystemDirectory, "drivers", "awealloc.sys"))) return false;
            using var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\awealloc");
            return service?.GetValue("Type") is int type && type == 1 && service.GetValue("Start") is int start && start is >= 0 and < 4;
        }
    }
    public bool IsAdministrator
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public MemorySnapshot GetMemory()
    {
        var state = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref state)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new(state.TotalPhysical, state.AvailablePhysical);
    }

    public DriveSnapshot Inspect(char letter)
    {
        if (letter is < 'A' or > 'Z') throw new ArgumentOutOfRangeException(nameof(letter));
        var buffer = new StringBuilder(32768);
        if (QueryDosDevice($"{letter}:", buffer, buffer.Capacity) == 0)
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 2)
            {
                // Also protect drive letters that are visible only through logical-drive enumeration.
                if ((GetLogicalDrives() & (1U << (letter - 'A'))) != 0) return new("occupied");
                return new(null);
            }
            throw new Win32Exception(error);
        }
        var path = buffer.ToString();
        // Avoid probing network, optical and unrelated disks, which may block for a long time.
        if (!new DriveSnapshot(path).IsImDisk) return new(path);
        if (!GetVolumeInformation($"{letter}:\\", null, 0, out var serial, out _, out _, null, 0))
            return new(path);
        if (!GetDiskFreeSpaceEx($"{letter}:\\", out _, out var total, out var free)) return new(path, serial);
        return new(path, serial, (long)total, (long)free, true);
    }

    public async Task<CommandResult> ExecuteAsync(IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo(DriverExecutable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            WorkingDirectory = Environment.SystemDirectory
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("ImDisk を起動できません。");
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        // Do not terminate a formatter midway. Keep the UI responsive while the operation finishes.
        await process.WaitForExitAsync();
        return new(process.ExitCode, await output, await error);
    }

    public void CreateTempFolder(DiskProfile profile)
    {
        if (!Inspect(profile.DriveLetter).IsOwnedBy(profile)) throw new IOException("作成したドライブが見つかりません。");
        Directory.CreateDirectory(Path.Combine(profile.RootPath, "Temp"));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus state);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string device, StringBuilder target, int length);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetLogicalDrives();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformation(string root, StringBuilder? label, int labelSize, out uint serial, out uint maxComponentLength, out uint flags, StringBuilder? fileSystem, int fileSystemSize);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string directory, out ulong freeAvailable, out ulong totalBytes, out ulong freeBytes);
}
