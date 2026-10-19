using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace SlskFs;

public static class NativeFs
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetAttr([MarshalAs(UnmanagedType.LPUTF8Str)] string path, ulong handle, out long size, out int directory);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, out ulong handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Read(ulong handle, long offset, IntPtr data, int length);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Release(ulong handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int OpenDir([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out ulong handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ReadDir(ulong handle, long offset, IntPtr data, IntPtr filler);
    [DllImport("slskfs_native", EntryPoint="slskfs_mount", CallingConvention=CallingConvention.Cdecl)]
    private static extern int MountNative(int argc, IntPtr argv, GetAttr ga, Open op, Read read, Release release, OpenDir od, ReadDir rd, Release rdir);
    [DllImport("slskfs_native", EntryPoint="slskfs_add_entry", CallingConvention=CallingConvention.Cdecl)]
    private static extern int AddEntry(IntPtr data, IntPtr filler, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, long next);
    public static int Errno(Exception ex)
    {
        Console.Error.WriteLine($"Filesystem operation failed: {ex.GetType().Name}: {ex.Message}");
        return ex switch
        {
            FileNotFoundException => -2, DirectoryNotFoundException => -2,
            UnauthorizedAccessException => -30, OperationCanceledException => -110,
            ArgumentException => -22, _ => -5
        };
    }
    public static unsafe int Mount(Engine engine, string mountpoint, bool allowOther)
    {
        var directories = new ConcurrentDictionary<ulong, string[]>();
        long directoryId = 0;
        GetAttr ga = (string path, ulong handle, out long size, out int directory) =>
        {
            size=0; directory=0;
            try
            {
                if (directories.ContainsKey(handle)) directory=1;
                else if (handle != 0) size = engine.HandleEntry((long)handle).Size;
                else if (engine.Catalog.IsDirectory(path)) directory=1;
                else size=engine.Catalog.FileEntry(path).Size;
                return 0;
            }
            catch (Exception ex) { return Errno(ex); }
        };
        Open op = (string path, int flags, out ulong handle) =>
        { handle=0; try { handle=(ulong)engine.Open(path, flags); return 0; } catch(Exception ex) { return Errno(ex); } };
        Read read = (ulong handle, long offset, IntPtr data, int length) =>
        { try { return engine.Read((long)handle, offset, new Span<byte>(data.ToPointer(), length)); } catch(Exception ex) { return Errno(ex); } };
        Release release = h => { try { engine.Release((long)h); } catch(Exception ex) { Errno(ex); } };
        OpenDir od = (string path, out ulong handle) =>
        {
            handle=0;
            try { var names = engine.Catalog.List(path); handle=(1UL << 63) | (ulong)Interlocked.Increment(ref directoryId);
                directories[handle]=[".", "..", ..names]; return 0; }
            catch(Exception ex) { return Errno(ex); }
        };
        ReadDir rd = (ulong handle, long offset, IntPtr data, IntPtr filler) =>
        {
            try
            {
                if (!directories.TryGetValue(handle, out var names)) return -9;
                if (offset < 0 || offset > names.Length) return -22;
                for (var i=(int)offset; i<names.Length; i++) if (AddEntry(data, filler, names[i], i+1)!=0) break;
                return 0;
            }
            catch(Exception ex) { return Errno(ex); }
        };
        Release rdir = h => directories.TryRemove(h, out _);
        var args = new[] { "soulfuse", "-f", "-o", "ro,default_permissions,fsname=soulfuse,subtype=soulfuse" +
            (allowOther ? ",allow_other" : ""), mountpoint };
        var strings = args.Select(Marshal.StringToCoTaskMemUTF8).ToArray();
        var argv = Marshal.AllocHGlobal(IntPtr.Size * strings.Length);
        try
        {
            Marshal.Copy(strings, 0, argv, strings.Length);
            return MountNative(args.Length, argv, ga, op, read, release, od, rd, rdir);
        }
        finally
        {
            GC.KeepAlive(ga); GC.KeepAlive(op); GC.KeepAlive(read); GC.KeepAlive(release);
            GC.KeepAlive(od); GC.KeepAlive(rd); GC.KeepAlive(rdir);
            foreach(var s in strings) Marshal.FreeCoTaskMem(s);
            Marshal.FreeHGlobal(argv);
        }
    }
}
