using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using AutoMailChecker.Core;

namespace AutoMailChecker.Windows.Credentials;

/// <summary>
/// Windows 資格情報マネージャーの汎用資格情報（CredRead / CredWrite）。中身は DPAPI で暗号化される。
/// </summary>
public sealed class WindowsCredentialStore : ICredentialStore
{
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    /// <summary>CRED_MAX_CREDENTIAL_BLOB_SIZE</summary>
    private const int MaxSecretBytes = 5 * 512;

    public string? ReadSecret(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (!NativeMethods.CredRead(key, CredTypeGeneric, 0, out var pointer))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == ErrorNotFound)
            {
                return null;
            }

            throw new Win32Exception(error, Translate.Text($"資格情報「{key}」を読めませんでした。", $"Could not read the credential \"{key}\"."));
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeMethods.Credential>(pointer);
            return credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0
                ? ""
                : Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / sizeof(char));
        }
        finally
        {
            NativeMethods.CredFree(pointer);
        }
    }

    public void WriteSecret(string key, string userName, string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(secret);
        var size = secret.Length * sizeof(char);
        if (size > MaxSecretBytes)
        {
            throw new ArgumentException(Translate.Text("パスワードが長すぎます。", "The password is too long."), nameof(secret));
        }

        var blob = Marshal.StringToCoTaskMemUni(secret);
        try
        {
            var credential = new NativeMethods.Credential
            {
                Type = CredTypeGeneric,
                TargetName = key,
                CredentialBlobSize = (uint)size,
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                UserName = userName,
            };
            if (!NativeMethods.CredWrite(ref credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), Translate.Text(
                    $"資格情報「{key}」を保存できませんでした。",
                    $"Could not save the credential \"{key}\"."));
            }
        }
        finally
        {
            // パスワードがメモリに残らないよう、ゼロで埋めてから解放する
            Marshal.ZeroFreeCoTaskMemUnicode(blob);
        }
    }

    /// <summary>テストの後始末用。画面から資格情報を消す操作は仕様にない。</summary>
    internal static bool Delete(string key) => NativeMethods.CredDelete(key, CredTypeGeneric, 0);

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct Credential
        {
            public uint Flags;
            public uint Type;
            public string TargetName;
            public string? Comment;
            public FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public string? TargetAlias;
            public string? UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credential);

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CredWrite(ref Credential credential, uint flags);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CredDelete(string target, uint type, uint flags);

        [DllImport("advapi32.dll")]
        public static extern void CredFree(IntPtr buffer);
    }
}
