using System;
using System.IO;
using System.Reflection;
using System.Security;
using System.Security.Cryptography;
using KillerScan.Services;

/// <summary>Release identity rules shared by the per-user and elevated update paths, protected
/// staging cleanup, and the local-file gate on the elevated helper's download argument.</summary>
internal static class ReleaseIdentityTests
{
    private const string Publisher = "Open Source Developer Stephen Riley";

    internal static void Run()
    {
        Identity();
        Staging();
        LocalFile();
    }

    private static void Identity()
    {
        var running = new Version(1, 9, 0, 0);
        AssemblyName Image(string name, string version) => new AssemblyName(name) { Version = new Version(version) };
        void Accept(string what, AssemblyName image, string tag, (bool, string) installed, (bool, string) incoming, bool strict = false)
        {
            try { ReleaseIdentity.Verify(image, tag, running, installed, incoming, strict); }
            catch (SecurityException error) { throw new InvalidOperationException("Rejected " + what + ": " + error.Message); }
        }
        void Refuse(string what, AssemblyName image, string tag, (bool, string) installed, (bool, string) incoming, bool strict = false)
        {
            try { ReleaseIdentity.Verify(image, tag, running, installed, incoming, strict); }
            catch (SecurityException) { return; }
            throw new InvalidOperationException("Accepted " + what + ".");
        }

        Accept("a newer release from the installed publisher", Image("KillerScan", "1.9.1.0"), "v1.9.1", (true, Publisher), (true, Publisher));
        Accept("a release signed by the publisher's renewed certificate", Image("KillerScan", "1.9.1.0"), "v1.9.1", (true, Publisher), (true, Publisher.ToUpperInvariant()));
        Accept("a signed release on an unsigned local build", Image("KillerScan", "1.9.1.0"), "1.9.1", (false, "(none)"), (true, Publisher));
        Refuse("an unsigned download", Image("KillerScan", "1.9.1.0"), "v1.9.1", (true, Publisher), (false, "(none)"));
        Refuse("an unsigned download on an unsigned build", Image("KillerScan", "1.9.1.0"), "v1.9.1", (false, "(none)"), (false, "(none)"));
        Refuse("another publisher's signature", Image("KillerScan", "1.9.1.0"), "v1.9.1", (true, Publisher), (true, "Open Source Developer Someone Else"));
        Refuse("a subject that only contains the publisher name", Image("KillerScan", "1.9.1.0"), "v1.9.1", (true, Publisher), (true, "Stephen Riley Imitations Ltd"));
        Refuse("an invalid signature from the publisher", Image("KillerScan", "1.9.1.0"), "v1.9.1", (true, Publisher), (false, Publisher));
        Refuse("another product", Image("KillerNotes", "1.9.1.0"), "v1.9.1", (true, Publisher), (true, Publisher));
        Refuse("the running version", Image("KillerScan", "1.9.0.0"), "v1.9.0", (true, Publisher), (true, Publisher));
        Refuse("an older signed release", Image("KillerScan", "1.8.3.0"), "v1.8.3", (true, Publisher), (true, Publisher));
        Refuse("an image whose version differs from its tag", Image("KillerScan", "1.9.2.0"), "v1.9.1", (true, Publisher), (true, Publisher));
        Refuse("a malformed tag", Image("KillerScan", "1.9.1.0"), "latest", (true, Publisher), (true, Publisher));
        Refuse("an unsigned elevated helper", Image("KillerScan", "1.9.1.0"), "1.9.1", (false, "(none)"), (true, Publisher), strict: true);
        Console.WriteLine("PASS update identity requires the installed publisher (any of its certificates), the product and a newer matching version.");

        // Without a seam for a real signature, an ordinary build of this runner is the unsigned image.
        string unsigned = Assembly.GetExecutingAssembly().Location;
        try { ReleaseIdentity.Verify(unsigned, "v99.0.0", requireInstalledSignature: false); }
        catch (SecurityException) { Console.WriteLine("PASS an unsigned executable on disk is refused."); return; }
        throw new InvalidOperationException("An unsigned executable on disk was accepted as an update.");
    }

    private static void Staging()
    {
        string root = Path.Combine(Path.GetTempPath(), "KillerScan-stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string download = Path.Combine(root, "download.exe");
            File.WriteAllText(download, "release bytes");
            string expected;
            using (var sha = SHA256.Create())
                expected = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(download))).Replace("-", "");
            string Staged() => Path.Combine(root, ".update-" + Guid.NewGuid().ToString("N") + ".exe");

            string early = Staged();
            Expect(() => TrustedUpdate.Stage(download, early, expected, _ => throw new SecurityException("unsigned")));
            if (File.Exists(early)) throw new InvalidOperationException("An update refused before staging still reached the installation folder.");

            string late = Staged();
            Expect(() => TrustedUpdate.Stage(download, late, expected, image =>
            { if (image == late) throw new SecurityException("staged bytes refused"); }));
            if (File.Exists(late)) throw new InvalidOperationException("A staged update that failed identity was left in the installation folder.");

            string tampered = Staged();
            Expect(() => TrustedUpdate.Stage(download, tampered, new string('0', 64), _ => { }));
            if (File.Exists(tampered)) throw new InvalidOperationException("A staged update that failed SHA-256 was left in the installation folder.");

            string occupied = Staged();
            File.WriteAllText(occupied, "someone else's file");
            Expect(() => TrustedUpdate.Stage(download, occupied, expected, _ => { }), typeof(IOException));
            if (File.ReadAllText(occupied) != "someone else's file")
                throw new InvalidOperationException("Staging removed a file it did not create.");

            string good = Staged();
            TrustedUpdate.Stage(download, good, expected, _ => { });
            if (File.ReadAllText(good) != "release bytes") throw new InvalidOperationException("A verified update was not staged.");
            if (Directory.GetFiles(root, "*.new").Length != 0) throw new InvalidOperationException("Staging left a partial copy behind.");
            Console.WriteLine("PASS refused updates never stay in the installation folder, and staging keeps files it did not create.");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void LocalFile()
    {
        string file = Path.GetTempFileName();
        string folder = Path.Combine(Path.GetTempPath(), "KillerScan-local-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            if (!string.Equals(ReleaseIdentity.RequireLocalFile(file), Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A local download was not accepted.");
            foreach (string path in new[] { @"\\server\share\KillerScan.exe", @"\\.\pipe\KillerScan", @"\\?\" + file, folder })
                Expect(() => ReleaseIdentity.RequireLocalFile(path), typeof(SecurityException), typeof(ArgumentException), typeof(NotSupportedException));
            Console.WriteLine("PASS the elevated helper accepts only an ordinary file on a local drive.");
        }
        finally { File.Delete(file); Directory.Delete(folder); }
    }

    private static void Expect(Action action, params Type[] accepted)
    {
        if (accepted.Length == 0) accepted = new[] { typeof(SecurityException) };
        try { action(); }
        catch (Exception error) when (Array.Exists(accepted, type => type.IsInstanceOfType(error))) { return; }
        throw new InvalidOperationException("A refused update path completed without the expected error.");
    }
}
