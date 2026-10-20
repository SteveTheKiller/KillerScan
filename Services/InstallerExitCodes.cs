#nullable enable
using System;
using System.ComponentModel;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Threading;

namespace KillerFamily.Deployment
{
    internal static class InstallerExitCodes
    {
        internal const int Success = 0;
        internal const int Failure = 1;
        internal const int AccessDenied = 5;
        internal const int MissingRuntime = 10;
        internal const int DiskFull = 112;
        internal const int Cancelled = 1223;
        internal const int Busy = 1618;
        internal const int SecurityRejected = 1625;

#if NET8_0_OR_GREATER
        private static readonly System.Buffers.SearchValues<char> PathSeparators = System.Buffers.SearchValues.Create("\\/");
#else
        private static readonly char[] PathSeparators = ['\\', '/'];
#endif

        internal static int FromException(Exception error)
        {
            for (Exception? current = error; current != null; current = current.InnerException)
            {
                if (current is OperationCanceledException) return Cancelled;
                if (current is UnauthorizedAccessException) return AccessDenied;
                if (current is SecurityException) return SecurityRejected;
                int code = current is Win32Exception native ? native.NativeErrorCode :
                    current is IOException ? current.HResult & 0xffff : 0;
                if (code == 39 || code == DiskFull) return DiskFull;
                // Sharing or lock violation: the copy being replaced is running. Report it as busy
                // so Intune, Configuration Manager and RMM tools retry instead of failing.
                if (code == 32 || code == 33) return Busy;
                if (code == AccessDenied || code == Cancelled || code == Busy || code == SecurityRejected)
                    return code;
            }
            return Failure;
        }

        /// <summary>
        /// True when the process has admin rights. A silent install run with them, as winget,
        /// Chocolatey and RMM tools run it, installs for all users. Run without them, as the
        /// Microsoft Store runs installers, it installs for the current user with no prompt.
        /// </summary>
        internal static bool IsElevated()
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        private static bool ContainsPathSeparator(string value) =>
#if NET8_0_OR_GREATER
            value.AsSpan().ContainsAny(PathSeparators);
#else
            value.IndexOfAny(PathSeparators) >= 0;
#endif

        internal static IDisposable Acquire(string product)
        {
            if (string.IsNullOrWhiteSpace(product) || ContainsPathSeparator(product))
                throw new ArgumentException("A product name is required.", nameof(product));
            var mutex = new Mutex(false, @"Global\KillerTools-Install-" + product);
            bool owned;
            try
            {
                try { owned = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { owned = true; }
            }
            catch { mutex.Dispose(); throw; }
            if (!owned)
            {
                mutex.Dispose();
                // Windows supplies this message in the user's language.
                throw new Win32Exception(Busy);
            }
            return new Lease(mutex);
        }

        private sealed class Lease : IDisposable
        {
            private Mutex? _mutex;
            internal Lease(Mutex mutex) { _mutex = mutex; }
            public void Dispose()
            {
                var mutex = _mutex;
                if (mutex == null) return;
                _mutex = null;
                try { mutex.ReleaseMutex(); }
                finally { mutex.Dispose(); }
            }
        }
    }
}
