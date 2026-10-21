using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FindIt.Client;

public sealed record EjectResult(bool Requested, string Message);

/// <summary>Requests Windows PnP removal after FindIt has closed all archive handles. Never forces a dismount.</summary>
public static class DriveEjector
{
    public static Task<EjectResult> RequestAsync(string rootPath) => Task.Run(() => Request(rootPath));

    private static EjectResult Request(string rootPath)
    {
        if (!OperatingSystem.IsWindows()) return new(false, "Use your operating system to eject this drive.");
        var drive = Path.GetPathRoot(Path.GetFullPath(rootPath));
        if (drive is null || drive.Length != 3 || string.Equals(drive, Path.GetPathRoot(Environment.SystemDirectory), StringComparison.OrdinalIgnoreCase))
            return new(false, "FindIt is closed. Use Windows to eject the external drive.");
        using var volume = CreateFile(@"\\.\" + drive[..2], 0, 7, 0, 3, 0, 0);
        if (volume.IsInvalid || !DeviceIoControl(volume, 0x002D1080, 0, 0, out DeviceNumber target, 12, out _, 0))
            return new(false, "FindIt is closed. Windows could not identify this external drive; use Safely Remove Hardware.");
        var diskGuid = new Guid("53f56307-b6bf-11d0-94f2-00a0c91efb8b");
        var set = SetupDiGetClassDevs(ref diskGuid, 0, 0, 0x12);
        if (set == new nint(-1)) return new(false, "FindIt is closed. Use Safely Remove Hardware.");
        try
        {
            for (uint index = 0; ; index++)
            {
                var data = new DeviceInterface { Size = (uint)Marshal.SizeOf<DeviceInterface>() };
                if (!SetupDiEnumDeviceInterfaces(set, 0, ref diskGuid, index, ref data)) break;
                var info = new DeviceInfo { Size = (uint)Marshal.SizeOf<DeviceInfo>() };
                SetupDiGetDeviceInterfaceDetail(set, ref data, 0, 0, out var required, ref info);
                if (required is 0 or > 65536) continue;
                var buffer = Marshal.AllocHGlobal((int)required);
                try
                {
                    Marshal.WriteInt32(buffer, nint.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref data, buffer, required, out _, ref info)) continue;
                    var path = Marshal.PtrToStringUni(buffer + 4);
                    if (path is null) continue;
                    using var disk = CreateFile(path, 0, 7, 0, 3, 0, 0);
                    if (disk.IsInvalid || !DeviceIoControl(disk, 0x002D1080, 0, 0, out DeviceNumber number, 12, out _, 0) || number.Number != target.Number) continue;
                    if (!GetHotplug(disk, 0x002D0C14, 0, 0, out Hotplug hotplug, 8, out _, 0) || !hotplug.DeviceHotplug)
                        return new(false, "FindIt is closed. This device does not advertise safe hot removal; use Windows to manage it.");
                    var veto = new StringBuilder(256);
                    var result = CM_Request_Device_Eject(info.DevInst, out _, veto, veto.Capacity, 0);
                    return result == 0
                        ? new(true, "Windows accepted the eject request. Wait for its safe-removal notification before unplugging.")
                        : new(false, "FindIt is closed, but Windows could not eject the drive. Close other apps using it and try Safely Remove Hardware.");
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            return new(false, "FindIt is closed. Use Safely Remove Hardware to eject this drive.");
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct DeviceNumber { public uint Type, Number, Partition; }
    [StructLayout(LayoutKind.Sequential)] private struct DeviceInterface { public uint Size; public Guid Class; public uint Flags; public nint Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct DeviceInfo { public uint Size; public Guid Class; public uint DevInst; public nint Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct Hotplug { public uint Size; [MarshalAs(UnmanagedType.U1)] public bool MediaRemovable; [MarshalAs(UnmanagedType.U1)] public bool MediaHotplug; [MarshalAs(UnmanagedType.U1)] public bool DeviceHotplug; [MarshalAs(UnmanagedType.U1)] public bool Override; }
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [DllImport("kernel32", SetLastError = true)] private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, nint input, uint inputSize, out DeviceNumber output, uint outputSize, out uint returned, nint overlap);
    [DllImport("kernel32", EntryPoint = "DeviceIoControl", SetLastError = true)] private static extern bool GetHotplug(SafeFileHandle handle, uint code, nint input, uint inputSize, out Hotplug output, uint outputSize, out uint returned, nint overlap);
    [DllImport("setupapi", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint SetupDiGetClassDevs(ref Guid guid, nint enumerator, nint parent, uint flags);
    [DllImport("setupapi", SetLastError = true)] private static extern bool SetupDiEnumDeviceInterfaces(nint set, nint info, ref Guid guid, uint index, ref DeviceInterface data);
    [DllImport("setupapi", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDeviceInterfaceDetail(nint set, ref DeviceInterface data, nint detail, uint detailSize, out uint required, ref DeviceInfo info);
    [DllImport("setupapi", SetLastError = true)] private static extern bool SetupDiDestroyDeviceInfoList(nint set);
    [DllImport("cfgmgr32", CharSet = CharSet.Unicode, EntryPoint = "CM_Request_Device_EjectW")] private static extern uint CM_Request_Device_Eject(uint instance, out uint vetoType, StringBuilder vetoName, int length, uint flags);
}
