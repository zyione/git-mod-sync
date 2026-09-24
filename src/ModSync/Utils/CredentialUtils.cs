using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace ModSync.Utils;

/// <summary>
/// Secure credential management using Windows Credential Manager (advapi32.dll)
/// with DPAPI (Data Protection API) fallback.
/// Tokens and passwords are NEVER stored in plaintext or in config.json.
/// </summary>
[SupportedOSPlatform("windows")]
public static class CredentialUtils
{
    private const string TargetPrefix = "ModSync_GitHub_";
    private const int CRED_TYPE_GENERIC = 1;
    private const int CRED_PERSIST_LOCAL_MACHINE = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite([In] ref CREDENTIAL userCredential, [In] int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree([In] IntPtr buffer);

    /// <summary>
    /// Saves a GitHub token securely in Windows Credential Manager.
    /// </summary>
    public static bool SaveCredential(string targetKey, string username, string secretToken)
    {
        string target = TargetPrefix + targetKey;

        try
        {
            byte[] secretBytes = Encoding.UTF8.GetBytes(secretToken);

            var credential = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = Marshal.StringToCoTaskMemUni(target),
                CredentialBlob = Marshal.AllocCoTaskMem(secretBytes.Length),
                CredentialBlobSize = secretBytes.Length,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                UserName = Marshal.StringToCoTaskMemUni(username)
            };

            try
            {
                Marshal.Copy(secretBytes, 0, credential.CredentialBlob, secretBytes.Length);
                bool success = CredWrite(ref credential, 0);
                if (success) return true;
            }
            finally
            {
                if (credential.TargetName != IntPtr.Zero) Marshal.FreeCoTaskMem(credential.TargetName);
                if (credential.CredentialBlob != IntPtr.Zero) Marshal.FreeCoTaskMem(credential.CredentialBlob);
                if (credential.UserName != IntPtr.Zero) Marshal.FreeCoTaskMem(credential.UserName);
            }
        }
        catch
        {
            // Fallback to DPAPI
        }

        return SaveWithDpapi(target, username, secretToken);
    }

    /// <summary>
    /// Retrieves a GitHub credential (username, secretToken) securely from Windows Credential Manager.
    /// </summary>
    public static (string? Username, string? SecretToken) LoadCredential(string targetKey)
    {
        string target = TargetPrefix + targetKey;

        try
        {
            if (CredRead(target, CRED_TYPE_GENERIC, 0, out IntPtr credPtr))
            {
                try
                {
                    var credential = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
                    string? user = Marshal.PtrToStringUni(credential.UserName);

                    if (credential.CredentialBlob != IntPtr.Zero && credential.CredentialBlobSize > 0)
                    {
                        byte[] secretBytes = new byte[credential.CredentialBlobSize];
                        Marshal.Copy(credential.CredentialBlob, secretBytes, 0, credential.CredentialBlobSize);
                        string token = Encoding.UTF8.GetString(secretBytes);
                        return (user, token);
                    }
                }
                finally
                {
                    CredFree(credPtr);
                }
            }
        }
        catch
        {
            // Fallback to DPAPI
        }

        return LoadWithDpapi(target);
    }

    /// <summary>
    /// Deletes a credential from Windows Credential Manager and DPAPI store.
    /// </summary>
    public static bool DeleteCredential(string targetKey)
    {
        string target = TargetPrefix + targetKey;
        bool deleted = false;

        try
        {
            deleted = CredDelete(target, CRED_TYPE_GENERIC, 0);
        }
        catch
        {
            // Ignore
        }

        DeleteDpapi(target);
        return deleted;
    }

    #region DPAPI Fallback

    private static string GetDpapiFilePath(string target)
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string folder = Path.Combine(localAppData, "ModSync", "security");
        PathUtils.EnsureDirectoryExists(folder);
        // Clean filename from target
        string safeFileName = string.Concat(target.Split(Path.GetInvalidFileNameChars())) + ".dat";
        return Path.Combine(folder, safeFileName);
    }

    private static bool SaveWithDpapi(string target, string username, string secretToken)
    {
        try
        {
            string payload = $"{username}:::{secretToken}";
            byte[] plainBytes = Encoding.UTF8.GetBytes(payload);
            byte[] encrypted = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(GetDpapiFilePath(target), encrypted);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static (string? Username, string? SecretToken) LoadWithDpapi(string target)
    {
        try
        {
            string path = GetDpapiFilePath(target);
            if (!File.Exists(path)) return (null, null);

            byte[] encrypted = File.ReadAllBytes(path);
            byte[] plainBytes = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            string payload = Encoding.UTF8.GetString(plainBytes);
            int split = payload.IndexOf(":::", StringComparison.Ordinal);
            if (split >= 0)
            {
                return (payload[..split], payload[(split + 3)..]);
            }
        }
        catch
        {
            // Decryption failure or corrupted
        }

        return (null, null);
    }

    private static void DeleteDpapi(string target)
    {
        try
        {
            string path = GetDpapiFilePath(target);
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    #endregion
}
