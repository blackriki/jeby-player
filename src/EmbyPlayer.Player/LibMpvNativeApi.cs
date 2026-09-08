using System.Runtime.InteropServices;
using System.Text;

namespace EmbyPlayer.Player;

internal sealed class LibMpvNativeApi : IMpvNativeApi
{
    private const string LibMpvName = "libmpv-2";
    private const int MpvFormatFlag = 3;
    private const int MpvFormatDouble = 5;

    public IntPtr Create()
    {
        return mpv_create();
    }

    public int Initialize(IntPtr context)
    {
        return mpv_initialize(context);
    }

    public int RequestLogMessages(IntPtr context, string minimumLevel)
    {
        return mpv_request_log_messages(context, ToNativeString(minimumLevel));
    }

    public int SetOptionString(IntPtr context, string name, string value)
    {
        return mpv_set_option_string(context, ToNativeString(name), ToNativeString(value));
    }

    public int SetPropertyString(IntPtr context, string name, string value)
    {
        return mpv_set_property_string(context, ToNativeString(name), ToNativeString(value));
    }

    public int Command(IntPtr context, IReadOnlyList<string> args)
    {
        var nativeArgs = new IntPtr[args.Count + 1];
        try
        {
            for (var i = 0; i < args.Count; i++)
            {
                nativeArgs[i] = AllocateNativeString(args[i]);
            }

            nativeArgs[^1] = IntPtr.Zero;
            return mpv_command(context, nativeArgs);
        }
        finally
        {
            foreach (var nativeArg in nativeArgs)
            {
                if (nativeArg != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(nativeArg);
                }
            }
        }
    }

    public MpvEventSnapshot WaitEvent(IntPtr context, double timeout)
    {
        var eventPointer = mpv_wait_event(context, timeout);
        if (eventPointer == IntPtr.Zero)
        {
            return new MpvEventSnapshot(MpvEventId.None, 0);
        }

        var mpvEvent = Marshal.PtrToStructure<MpvEvent>(eventPointer);
        if (mpvEvent.EventId == MpvEventId.LogMessage && mpvEvent.Data != IntPtr.Zero)
        {
            var logMessage = Marshal.PtrToStructure<MpvEventLogMessage>(mpvEvent.Data);
            var text = Marshal.PtrToStringUTF8(logMessage.Text);
            return new MpvEventSnapshot(
                mpvEvent.EventId,
                mpvEvent.Error,
                LogMessageKeywords: ExtractLogKeywords(text));
        }

        if (mpvEvent.EventId != MpvEventId.EndFile || mpvEvent.Data == IntPtr.Zero)
        {
            return new MpvEventSnapshot(mpvEvent.EventId, mpvEvent.Error);
        }

        var endFile = Marshal.PtrToStructure<MpvEventEndFile>(mpvEvent.Data);
        return new MpvEventSnapshot(
            mpvEvent.EventId,
            mpvEvent.Error,
            endFile.Reason,
            endFile.Error);
    }

    public string? GetPropertyString(IntPtr context, string name)
    {
        var pointer = mpv_get_property_string(context, ToNativeString(name));
        if (pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUTF8(pointer);
        }
        finally
        {
            mpv_free(pointer);
        }
    }

    public bool? GetFlagProperty(IntPtr context, string name)
    {
        var value = 0;
        var result = mpv_get_property(context, ToNativeString(name), MpvFormatFlag, ref value);
        return result < 0 ? null : value != 0;
    }

    public double? GetDoubleProperty(IntPtr context, string name)
    {
        var value = 0d;
        var result = mpv_get_property_double(context, ToNativeString(name), MpvFormatDouble, ref value);
        return result < 0 ? null : value;
    }

    public void Wakeup(IntPtr context)
    {
        mpv_wakeup(context);
    }

    public void TerminateDestroy(IntPtr context)
    {
        mpv_terminate_destroy(context);
    }

    private static byte[] ToNativeString(string value)
    {
        return Encoding.UTF8.GetBytes(value + '\0');
    }

    private static IntPtr AllocateNativeString(string value)
    {
        var bytes = ToNativeString(value);
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        return pointer;
    }

    [DllImport(LibMpvName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr mpv_create();

    [DllImport(LibMpvName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mpv_initialize(IntPtr ctx);

    [DllImport(LibMpvName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mpv_request_log_messages(
        IntPtr ctx,
        byte[] minLevel);

    [DllImport(LibMpvName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mpv_set_option_string(
        IntPtr ctx,
        byte[] name,
        byte[] data);

    [DllImport(LibMpvName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mpv_set_property_string(
        IntPtr ctx,
        byte[] name,
        byte[] data);

    [DllImport(LibMpvName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mpv_command(
        IntPtr ctx,
        [In] IntPtr[] args);

    [DllImport(LibMpvName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr mpv_wait_event(
        IntPtr ctx,
        double timeout);

    [DllImport(LibMpvName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr mpv_get_property_string(
        IntPtr ctx,
        byte[] name);

    [DllImport(LibMpvName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mpv_get_property(
        IntPtr ctx,
        byte[] name,
        int format,
        ref int data);

    [DllImport(LibMpvName, EntryPoint = "mpv_get_property", CallingConvention = CallingConvention.Cdecl)]
    private static extern int mpv_get_property_double(
        IntPtr ctx,
        byte[] name,
        int format,
        ref double data);

    [DllImport(LibMpvName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void mpv_free(IntPtr data);

    [DllImport(LibMpvName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void mpv_wakeup(IntPtr ctx);

    [DllImport(LibMpvName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void mpv_terminate_destroy(IntPtr ctx);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct MpvEvent
    {
        public readonly MpvEventId EventId;
        public readonly int Error;
        public readonly ulong ReplyUserData;
        public readonly IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct MpvEventEndFile
    {
        public readonly int Reason;
        public readonly int Error;
        public readonly long PlaylistEntryId;
        public readonly int PlaylistInsertId;
        public readonly int PlaylistInsertNumEntries;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct MpvEventLogMessage
    {
        public readonly IntPtr Prefix;
        public readonly IntPtr Level;
        public readonly IntPtr Text;
        public readonly int LogLevel;
    }

    private static string? ExtractLogKeywords(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var keywords = new[]
        {
            "401",
            "403",
            "404",
            "forbidden",
            "unauthorized",
            "cannot open",
            "demux",
            "codec",
            "tls"
        };
        var found = keywords
            .Where(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return found.Length == 0 ? null : string.Join(",", found);
    }
}
