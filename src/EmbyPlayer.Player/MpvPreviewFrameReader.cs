using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace EmbyPlayer.Player;

internal static class MpvPreviewFrameReader
{
    // screenshot-raw returns memory owned by mpv_node; copy before freeing it.
    public static byte[]? CaptureBitmap(IntPtr handle)
    {
        var args = new[] { "screenshot-raw", "video", "bgra" }.Select(Marshal.StringToCoTaskMemUTF8).Append(IntPtr.Zero).ToArray();
        MpvNode result = default;
        try
        {
            if (mpv_command_ret(handle, args, out result) < 0 || result.Format != 8) return null;
            var map = Marshal.PtrToStructure<MpvNodeList>(result.Pointer);
            if (map.Count is < 1 or > 16 || map.Values == IntPtr.Zero || map.Keys == IntPtr.Zero) return null;
            long width = 0, height = 0, stride = 0;
            MpvByteArray pixels = default;
            var format = string.Empty;
            for (var i = 0; i < map.Count; i++)
            {
                var key = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(map.Keys, i * IntPtr.Size));
                var value = Marshal.PtrToStructure<MpvNode>(map.Values + i * Marshal.SizeOf<MpvNode>());
                if (key == "w" && value.Format == 4) width = value.Integer;
                if (key == "h" && value.Format == 4) height = value.Integer;
                if (key == "stride" && value.Format == 4) stride = value.Integer;
                if (key == "format" && value.Format == 1) format = Marshal.PtrToStringUTF8(value.Pointer);
                if (key == "data" && value.Format == 9) pixels = Marshal.PtrToStructure<MpvByteArray>(value.Pointer);
            }
            if (width is < 1 or > 640 || height is < 1 or > 640 || format != "bgra"
                || stride < width * 4 || stride > 4096 || pixels.Data == IntPtr.Zero
                || pixels.Size > 2 * 1024 * 1024 || (ulong)(stride * height) > pixels.Size) return null;
            var rowBytes = checked((int)width * 4);
            var bitmap = new byte[checked(54 + rowBytes * (int)height)];
            bitmap[0] = (byte)'B'; bitmap[1] = (byte)'M';
            BinaryPrimitives.WriteInt32LittleEndian(bitmap.AsSpan(2), bitmap.Length);
            BinaryPrimitives.WriteInt32LittleEndian(bitmap.AsSpan(10), 54);
            BinaryPrimitives.WriteInt32LittleEndian(bitmap.AsSpan(14), 40);
            BinaryPrimitives.WriteInt32LittleEndian(bitmap.AsSpan(18), (int)width);
            BinaryPrimitives.WriteInt32LittleEndian(bitmap.AsSpan(22), -(int)height);
            BinaryPrimitives.WriteInt16LittleEndian(bitmap.AsSpan(26), 1);
            BinaryPrimitives.WriteInt16LittleEndian(bitmap.AsSpan(28), 32);
            for (var row = 0; row < height; row++)
                Marshal.Copy(pixels.Data + checked((int)(row * stride)), bitmap, 54 + row * rowBytes, rowBytes);
            return bitmap;
        }
        finally
        {
            mpv_free_node_contents(ref result);
            foreach (var arg in args) if (arg != IntPtr.Zero) Marshal.FreeCoTaskMem(arg);
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct MpvNode
    {
        [FieldOffset(0)] public long Integer;
        [FieldOffset(0)] public IntPtr Pointer;
        [FieldOffset(8)] public int Format;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MpvNodeList { public int Count; public IntPtr Values; public IntPtr Keys; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MpvByteArray { public IntPtr Data; public nuint Size; }
    [DllImport("libmpv-2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int mpv_command_ret(IntPtr handle, [In] IntPtr[] args, out MpvNode result);
    [DllImport("libmpv-2", CallingConvention = CallingConvention.Cdecl)]
    private static extern void mpv_free_node_contents(ref MpvNode result);
}
