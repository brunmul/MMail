using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using MMail.Core.Mail;

namespace MMail.Core.Platform;

/// Passwords in the Windows Credential Manager ("Generic credentials", MMail:…),
/// readable only by the signed-in user.
[SupportedOSPlatform("windows")]
public sealed partial class WindowsCredentialStore : ICredentialStore
{
    private const string Prefix = "MMail:";
    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref Credential credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    public string? Get(string key)
    {
        if (!CredRead(Prefix + key, CredTypeGeneric, 0, out var ptr)) return null;
        try
        {
            var cred = Marshal.PtrToStructure<Credential>(ptr);
            if (cred.CredentialBlob == IntPtr.Zero || cred.CredentialBlobSize == 0) return "";
            var bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.Unicode.GetString(bytes);
        }
        finally
        {
            CredFree(ptr);
        }
    }

    public void Set(string key, string secret)
    {
        var bytes = Encoding.Unicode.GetBytes(secret);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var cred = new Credential
            {
                Type = CredTypeGeneric, TargetName = Prefix + key, UserName = key,
                CredentialBlob = blob, CredentialBlobSize = bytes.Length, Persist = CredPersistLocalMachine,
            };
            if (!CredWrite(ref cred, 0)) throw new InvalidOperationException($"Could not save the password (error {Marshal.GetLastWin32Error()}).");
        }
        finally
        {
            Marshal.FreeHGlobal(blob);
        }
    }

    public void Delete(string key) => CredDelete(Prefix + key, CredTypeGeneric, 0);
}
