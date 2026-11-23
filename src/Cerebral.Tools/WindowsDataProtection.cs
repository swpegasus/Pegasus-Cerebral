using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Cerebral.Tools;

internal static class WindowsDataProtection
{
    private const int UiForbidden = 0x1;

    public static byte[] Protect(byte[] value, byte[] entropy) => Transform(value, entropy, protect: true);
    public static byte[] Unprotect(byte[] value, byte[] entropy) => Transform(value, entropy, protect: false);
    public static byte[] Unprotect(byte[] value) => Transform(value, [], protect: false);

    private static byte[] Transform(byte[] value, byte[] entropy, bool protect)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows protected storage is unavailable on this platform.");
        var input = Blob.From(value);
        var optional = Blob.From(entropy);
        try
        {
            var ok = protect
                ? CryptProtectData(ref input, null, ref optional, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref optional, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var result = new byte[output.Length];
                if (result.Length > 0) Marshal.Copy(output.Data, result, 0, result.Length);
                return result;
            }
            finally
            {
                if (output.Data != IntPtr.Zero)
                {
                    for (var index = 0; index < output.Length; index++) Marshal.WriteByte(output.Data, index, 0);
                    LocalFree(output.Data);
                }
            }
        }
        finally
        {
            input.Dispose(); optional.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob
    {
        public int Length;
        public IntPtr Data;

        public static Blob From(byte[] value)
        {
            var blob = new Blob { Length = value.Length, Data = value.Length == 0 ? IntPtr.Zero : Marshal.AllocHGlobal(value.Length) };
            if (value.Length > 0) Marshal.Copy(value, 0, blob.Data, value.Length);
            return blob;
        }

        public void Dispose()
        {
            if (Data == IntPtr.Zero) return;
            if (Length > 0) Marshal.Copy(new byte[Length], 0, Data, Length);
            Marshal.FreeHGlobal(Data); Data = IntPtr.Zero; Length = 0;
        }
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string? description, ref Blob entropy,
        IntPtr reserved, IntPtr prompt, int flags, out Blob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, ref Blob entropy,
        IntPtr reserved, IntPtr prompt, int flags, out Blob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
