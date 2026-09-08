using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using EmbyPlayer.Core.Authentication;

namespace EmbyPlayer.App.Security;

public sealed class WindowsCredentialAuthSessionStore : IAuthSessionTokenStore
{
    private const uint CredentialTypeGeneric = 1;
    private const uint CredentialPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    public Task SaveTokenAsync(
        string credentialTarget,
        string accessToken,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(credentialTarget))
        {
            throw new ArgumentException("Credential target is required.", nameof(credentialTarget));
        }

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new ArgumentException("Access token is required.", nameof(accessToken));
        }

        var tokenBytes = Encoding.Unicode.GetBytes(accessToken);
        var tokenPointer = Marshal.AllocHGlobal(tokenBytes.Length);

        try
        {
            Marshal.Copy(tokenBytes, 0, tokenPointer, tokenBytes.Length);
            var credential = new NativeCredential
            {
                Type = CredentialTypeGeneric,
                TargetName = credentialTarget,
                CredentialBlobSize = (uint)tokenBytes.Length,
                CredentialBlob = tokenPointer,
                Persist = CredentialPersistLocalMachine,
                UserName = "EmbyPlayer"
            };

            if (!CredWrite(ref credential, 0))
            {
                throw CreateCredentialException("Failed to save auth session credential.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(tokenPointer);
        }

        return Task.CompletedTask;
    }

    public Task<string?> LoadTokenAsync(string credentialTarget, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(credentialTarget))
        {
            throw new ArgumentException("Credential target is required.", nameof(credentialTarget));
        }

        if (!CredRead(credentialTarget, CredentialTypeGeneric, 0, out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                return Task.FromResult<string?>(null);
            }

            throw CreateCredentialException("Failed to load auth session credential.", error);
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                return Task.FromResult<string?>(null);
            }

            var credentialBlobSize = checked((int)credential.CredentialBlobSize);
            var tokenBytes = new byte[credentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, tokenBytes, 0, tokenBytes.Length);
            return Task.FromResult<string?>(Encoding.Unicode.GetString(tokenBytes));
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    public Task DeleteTokenAsync(string credentialTarget, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(credentialTarget))
        {
            throw new ArgumentException("Credential target is required.", nameof(credentialTarget));
        }

        if (!CredDelete(credentialTarget, CredentialTypeGeneric, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound)
            {
                throw CreateCredentialException("Failed to delete auth session credential.", error);
            }
        }

        return Task.CompletedTask;
    }

    private static Win32Exception CreateCredentialException(string message)
    {
        return CreateCredentialException(message, Marshal.GetLastWin32Error());
    }

    private static Win32Exception CreateCredentialException(string message, int error)
    {
        return new Win32Exception(error, message);
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredRead(
        string target,
        uint type,
        uint reservedFlag,
        out IntPtr credential);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }
}
