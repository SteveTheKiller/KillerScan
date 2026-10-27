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
            try { return GetSignerInfo(Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty); }
            catch { return (false, "(unavailable)", "(none)"); }
        }

        internal static (bool valid, string subject, string thumb) GetSignerInfo(string exePath)
        {
            var (valid, subject, thumb, _) = Inspect(exePath);
            return (valid, subject, thumb);
        }

        /// <summary>
        /// Whether <paramref name="candidate"/> may replace the signed exe at
        /// <paramref name="current"/>. A copy with a valid signature only accepts a replacement
        /// whose signature also validates and names the same publisher (<see cref="SamePublisher"/>).
        /// Publisher identity is the certificate subject's common name, never the thumbprint: a renewed
        /// certificate has a new thumbprint, and can carry new address fields, for the same publisher.
        /// A copy whose own signature is present but fails accepts nothing. An unsigned copy (a local or
        /// development build) has no publisher to hold a replacement to, and accepts it as before.
        /// </summary>
        internal static bool MayReplace(string current, string candidate)
        {
            try
            {
                // Keep the current bytes stable while distinguishing an unsigned local image
                // from a signed image whose verification failed or could not be completed.
                using var currentImage = new FileStream(current, FileMode.Open, FileAccess.Read, FileShare.Read);
                var (currentValid, currentSubject, _, currentPublisher) = Inspect(current);
                if (!currentValid)
                    return currentPublisher.Length == 0 && IsUnsignedImage(currentImage);
                var (candidateValid, candidateSubject, _, _) = Inspect(candidate);
                return candidateValid && SamePublisher(currentSubject, candidateSubject);
            }
            catch { return false; }
        }

        /// <summary>True when two certificate subjects, as simple names (the common name), name the same
        /// publisher. Case-insensitive; an empty or placeholder subject never matches. Thumbprints play
        /// no part, so a renewed certificate of the same publisher matches.</summary>
        internal static bool SamePublisher(string? current, string? candidate)
        {
            current = current?.Trim(); candidate = candidate?.Trim();
            return !string.IsNullOrEmpty(current) && !string.IsNullOrEmpty(candidate)
                && current != "(not signed)" && current != "(unavailable)"
                && string.Equals(current, candidate, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsUnsignedImage(FileStream image)
        {
            // A missing certificate is conclusive only for a readable PE with an empty
            // certificate directory. A malformed or unreadable signature must fail closed.
            using var reader = new BinaryReader(image, System.Text.Encoding.UTF8, leaveOpen: true);
            if (image.Length < 64 || reader.ReadUInt16() != 0x5a4d) return false;
            image.Position = 0x3c;
            uint pe = reader.ReadUInt32();
            if (pe < 64 || (long)pe + 24 > image.Length) return false;
            image.Position = pe;
            if (reader.ReadUInt32() != 0x00004550) return false;
            image.Position = pe + 20;
            ushort optionalSize = reader.ReadUInt16();
            long optional = (long)pe + 24;
            if (optional + optionalSize > image.Length) return false;
            image.Position = optional;
            ushort magic = reader.ReadUInt16();
            int directories = magic == 0x10b ? 96 : magic == 0x20b ? 112 : 0;
            if (directories == 0 || optionalSize < directories + 40) return false;
            image.Position = optional + directories - 4;
            if (reader.ReadUInt32() < 5) return false;
            image.Position = optional + directories + 32;
            return reader.ReadUInt32() == 0 && reader.ReadUInt32() == 0;
        }

        private static (bool valid, string subject, string thumb, string publisher) Inspect(string exePath)
        {
            var subject = "(not signed)";
            var thumb   = "(none)";
            string publisher;

            try
            {
                if (exePath.Length == 0) return (false, "(unavailable)", "(none)", string.Empty);
#pragma warning disable SYSLIB0057 // Authenticode signer extraction has no X509CertificateLoader equivalent.
                using var sourceCertificate = X509Certificate.CreateFromSignedFile(exePath);
                using var cert = new X509Certificate2(sourceCertificate);
#pragma warning restore SYSLIB0057
                var subj = cert.GetNameInfo(X509NameType.SimpleName, false);
                subject   = string.IsNullOrEmpty(subj) ? cert.Subject : subj;
                thumb     = string.IsNullOrEmpty(cert.Thumbprint) ? "(none)" : cert.Thumbprint;
                publisher = cert.Subject ?? string.Empty;
            }
            catch { return (false, "(not signed)", "(none)", string.Empty); }

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
                return (WinVerifyTrust(IntPtr.Zero, ref actionId, dataPtr) == 0, subject, thumb, publisher);
            }
            catch { return (false, subject, thumb, publisher); }
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
