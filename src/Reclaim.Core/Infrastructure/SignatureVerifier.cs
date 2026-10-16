using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace Reclaim.Core.Infrastructure;

public enum SignatureStatus { Unknown, Signed, Unsigned, Invalid, Missing }

public sealed record SignatureInfo(SignatureStatus Status, string? Signer);

/// <summary>Authenticode check through WinVerifyTrust (no revocation check to stay offline-fast).</summary>
public static class SignatureVerifier
{
    private static readonly Guid ActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public static SignatureInfo Verify(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return new(SignatureStatus.Missing, null);
        var status = Check(path);
        string? signer = null;
        if (status == SignatureStatus.Signed)
        {
            try
            {
#pragma warning disable SYSLIB0057
                using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
                signer = cert.GetNameInfo(X509NameType.SimpleName, false);
            }
            catch
            {
                // catalog-signed files have no embedded certificate; status stays Signed
            }
        }
        return new(status, signer);
    }

    private static SignatureStatus Check(string path)
    {
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = path,
        };
        var pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        Marshal.StructureToPtr(fileInfo, pFile, false);
        var data = new WINTRUST_DATA
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
            dwUIChoice = 2, // WTD_UI_NONE
            fdwRevocationChecks = 0,
            dwUnionChoice = 1, // WTD_CHOICE_FILE
            pFile = pFile,
            dwStateAction = 0,
            dwProvFlags = 0x00000040 | 0x00001000, // WTD_REVOCATION_CHECK_NONE | WTD_CACHE_ONLY_URL_RETRIEVAL
        };
        try
        {
            var action = ActionGenericVerifyV2;
            var result = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            return result switch
            {
                0 => SignatureStatus.Signed,
                unchecked((int)0x800B0100) => SignatureStatus.Unsigned, // TRUST_E_NOSIGNATURE
                unchecked((int)0x800B0003) => SignatureStatus.Unsigned, // TRUST_E_SUBJECT_FORM_UNKNOWN
                _ => SignatureStatus.Invalid,
            };
        }
        catch
        {
            return SignatureStatus.Unknown;
        }
        finally
        {
            Marshal.FreeHGlobal(pFile);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);
}
