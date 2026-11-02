using System.Runtime.InteropServices;

namespace Fleet.Agent;

/// <summary>No-follow Linux metadata with errno preserved; failure is never absence.</summary>
internal static class StockNativePathProbe
{
    internal enum State { Present, Absent, Unobservable }
    internal readonly record struct Result(State Status, ushort Mode = 0, uint? Owner = null, int Error = 0);
    // The complete statx ABI is 256 bytes, including fields/reserved bytes not consumed here.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    internal struct Statx
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(20)] public uint Uid;
        [FieldOffset(28)] public ushort Mode;
    }
    internal readonly record struct SyscallResult(int ReturnCode, int Error, Statx Buffer);
    internal delegate SyscallResult NativeCall(string path);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int StatxCall(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags, uint mask, out Statx buffer);

    internal static SyscallResult Read(string path)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        var code = StatxCall(-100, path, 0x100, 0x3, out var buffer);
        var error = code == 0 ? 0 : Marshal.GetLastPInvokeError();
        return new(code, error, buffer);
    }

    internal static Result Probe(string path, NativeCall? syscall = null)
    {
        try
        {
            var result = (syscall ?? Read)(path);
            if (result.ReturnCode == -1)
                return result.Error == 2 ? new(State.Absent) : new(State.Unobservable, Error: result.Error);
            if (result.ReturnCode != 0 || (result.Buffer.Mask & 0x3) != 0x3)
                return new(State.Unobservable);
            return new(State.Present, result.Buffer.Mode, (result.Buffer.Mask & 0x8) != 0 ? result.Buffer.Uid : null);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException or PlatformNotSupportedException
            or ArgumentException or MarshalDirectiveException)
        { return new(State.Unobservable); }
    }
}
