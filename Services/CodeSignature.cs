using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace KillerScan.Services
{
    /// <summary>
    /// Authenticode state and content hash of the running exe.
    ///
    /// Reading a certificate out of a file only proves a certificate is embedded; it does not prove
    /// the signature validates. WinVerifyTrust does the full chain check, so a tampered or badly
    /// signed build cannot present its publisher as verified.
    /// </summary>
    internal static class CodeSignature
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct WINTRUST_FILE_INFO
        {
            public uint   cbStruct;
            public IntPtr pcwszFilePath;   // LPCWSTR
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINTRUST_DATA
        {
            public uint   cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint   dwUIChoice;          // 2 = WTD_UI_NONE
            public uint   fdwRevocationChecks; // 0 = WTD_REVOKE_NONE
            public uint   dwUnionChoice;       // 1 = WTD_CHOICE_FILE
            public IntPtr pUnion;              // -> WINTRUST_FILE_INFO
            public uint   dwStateAction;       // 0 = WTD_STATEACTION_IGNORE
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint   dwProvFlags;         // 0x1000 = WTD_CACHE_ONLY_URL_RETRIEVAL
            public uint   dwUIContext;
            public IntPtr pSignatureSettings;
        }

        private static readonly Guid WTD_VERIFY_GENERIC =
            new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false,
                   CharSet = CharSet.Unicode)]
        private static extern uint WinVerifyTrust(
            IntPtr hwnd, ref Guid pgActionID, IntPtr pWVTData);

        /// <summary>Reads the running exe's Authenticode certificate for display and validates the
        /// signature. Valid is false for unsigned, expired or tampered files. Retrieval is cache-only
        /// (0x1000), so nothing here waits on the network: a signed exe carries its own intermediates,
        /// so the chain still builds offline.</summary>
        internal static (bool valid, string subject, string thumb) GetSignerInfo()
        {
            var subject = "(not signed)";
            var thumb   = "(none)";
            string exePath;

            try
            {
                exePath = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
                if (exePath.Length == 0) return (false, "(unavailable)", "(none)");
                using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(exePath));
                var subj = cert.GetNameInfo(X509NameType.SimpleName, false);
                subject = string.IsNullOrEmpty(subj) ? cert.Subject : subj;
                thumb   = string.IsNullOrEmpty(cert.Thumbprint) ? "(none)" : cert.Thumbprint;
            }
            catch { return (false, "(not signed)", "(none)"); }

            var pathPtr     = Marshal.StringToHGlobalUni(exePath);
            var fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
            var dataPtr     = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_DATA>());
            try
            {
                Marshal.StructureToPtr(new WINTRUST_FILE_INFO
                {
                    cbStruct      = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                    pcwszFilePath = pathPtr
                }, fileInfoPtr, false);

                Marshal.StructureToPtr(new WINTRUST_DATA
                {
                    cbStruct      = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                    dwUIChoice    = 2,       // WTD_UI_NONE
                    dwUnionChoice = 1,       // WTD_CHOICE_FILE
                    pUnion        = fileInfoPtr,
                    dwProvFlags   = 0x1000   // WTD_CACHE_ONLY_URL_RETRIEVAL, never hit the network
                }, dataPtr, false);

                var actionId = WTD_VERIFY_GENERIC;
                return (WinVerifyTrust(IntPtr.Zero, ref actionId, dataPtr) == 0, subject, thumb);
            }
            catch { return (false, subject, thumb); }
            finally
            {
                Marshal.FreeHGlobal(dataPtr);
                Marshal.FreeHGlobal(fileInfoPtr);
                Marshal.FreeHGlobal(pathPtr);
            }
        }

        /// <summary>SHA-256 of the running exe as lower-case hex. Slow on a large file, so callers
        /// run it off the UI thread.</summary>
        internal static string ExeSha256()
        {
            try
            {
                var path = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "(unavailable)";
                using var sha = SHA256.Create();
                using var fs  = File.OpenRead(path);
                return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
            }
            catch { return "(unavailable)"; }
        }
    }
}
