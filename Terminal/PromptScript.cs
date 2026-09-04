using System;
using System.IO;
using System.Reflection;
using System.Text;

// KillerScan's own PowerShell prompt: where the script lives, and how it reaches a shell.
//
// PowerShell loads $PROFILE first and only then runs anything passed on the command line, so
// dot-sourcing the script from -Command lands AFTER the user's profile and its prompt wins -
// inside KillerScan and nowhere else. No file the user maintains is written to, and their
// Windows Terminal, VS Code and any pwsh they start themselves are untouched.
//
// The unpacked copy is the USER'S. It is written once and never overwritten, because the whole
// point of shipping a snippet rather than a compiled-in prompt is that it can be edited. A
// newer KillerScan drops its version beside it as KillerPrompt.default.ps1, so an upgrade can
// be diffed and taken deliberately instead of arriving as a silent stomp on a customization.
//
// Deliberately PowerShell only. cmd's prompt is a PROMPT environment string with no way to run
// code per line, so it cannot show a branch, an exit code or a duration - a cmd prompt would be
// a different, much poorer feature wearing the same name.
namespace KillerScan.Shell
{
    public partial class MainWindow
    {
        private const string PromptResource = "KillerScan.Terminal.KillerPrompt.ps1";

        /// <summary>The user's copy, editable in place and never overwritten by an upgrade.</summary>
        internal static string PromptScriptPath => Path.Combine(PromptDir, "KillerPrompt.ps1");

        /// <summary>The shipped copy, refreshed on every launch so it always matches the exe.</summary>
        private static string PromptDefaultPath => Path.Combine(PromptDir, "KillerPrompt.default.ps1");

        private static string PromptDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KillerScan", "prompt");

        // ═══════════════════════════════════════════════════════════
        //  UNPACK
        // ═══════════════════════════════════════════════════════════
        /// <summary>
        /// Make sure the script is on disk. Safe to call repeatedly; only the reference copy is
        /// rewritten after the first run.
        /// </summary>
        internal static void EnsurePromptScript()
        {
            try
            {
                Directory.CreateDirectory(PromptDir);

                string shipped = ReadResource();
                if (shipped.Length == 0) return;

                // Always current, so "what changed in this version" has an
                // answer without needing the exe unpacked again.
                WriteIfDifferent(PromptDefaultPath, shipped);

                // The user's copy, only if it is not there. An edit survives every upgrade.
                if (!File.Exists(PromptScriptPath))
                    File.WriteAllText(PromptScriptPath, shipped, new UTF8Encoding(true));
            }
            catch { /* no prompt is a cosmetic loss; never let it stop a shell opening */ }
        }

        // A BOM on the way out, unlike everywhere else in this project: PowerShell 5.1 reads a
        // BOM-less file as the system ANSI codepage, which turns every box-drawing and powerline
        // glyph in the script into mojibake. 7 defaults to UTF-8 and does not care either way.
        private static void WriteIfDifferent(string path, string content)
        {
            try
            {
                if (File.Exists(path) && File.ReadAllText(path) == content) return;
                File.WriteAllText(path, content, new UTF8Encoding(true));
            }
            catch { }
        }

        private static string ReadResource()
        {
            try
            {
                using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(PromptResource);
                if (s == null) return string.Empty;
                using var r = new StreamReader(s, Encoding.UTF8);
                return r.ReadToEnd();
            }
            catch { return string.Empty; }
        }

        // ═══════════════════════════════════════════════════════════
        //  INJECTION
        // ═══════════════════════════════════════════════════════════
        /// <summary>
        /// The arguments that dot-source the script, or an empty string when the prompt is
        /// switched off or the file is missing.
        /// </summary>
        /// <remarks>
        /// -NoExit is what keeps the shell interactive after the command runs; without it
        /// PowerShell would dot-source the script and immediately exit, closing the tab.
        /// </remarks>
        /// <param name="then">
        /// A command to run once the prompt is in place, or null for a bare shell. It rides
        /// inside the same -Command as the prompt, because PowerShell takes only one. Callers
        /// pass shell text they built themselves; anything carrying user input goes to
        /// TerminalControl.Start as its own executable instead, never through here.
        /// </param>
        internal static string PromptArgs(string? then = null)
        {
            string tail = string.IsNullOrEmpty(then) ? string.Empty : "; " + then;

            // Set KS_PROMPT=0 to opt out for good. Checked here rather than in the script so
            // opting out also skips the unpack and the command line stays clean.
            if (Environment.GetEnvironmentVariable("KS_PROMPT") == "0")
                return tail.Length == 0 ? " -NoExit" : " -NoExit -Command \"" + then + "\"";

            EnsurePromptScript();
            if (!File.Exists(PromptScriptPath))
                return tail.Length == 0 ? " -NoExit" : " -NoExit -Command \"" + then + "\"";

            // Single quotes inside the double-quoted -Command argument, so a path containing a
            // space needs no further escaping. A literal single quote in the path is doubled,
            // which is how PowerShell escapes one inside a single-quoted string.
            string safe = PromptScriptPath.Replace("'", "''");

            // AFTER the script (and the $PROFILE before it) has settled on a prompt, wrap
            // whichever function won with a cwd reporter: an OSC 9;9 per render is what lets the
            // terminal's reported directory follow a cd (TerminalBuffer.OscDispatch case 9).
            // Wrapped HERE rather than inside KillerPrompt.ps1
            // because the user's unpacked copy is never overwritten - a fix in the .ps1 would
            // reach no existing install - and because it has to survive a fully custom prompt
            // too. FileSystem-only: a registry or cert location must not become the tab's
            // RootPath. No double quotes anywhere in the wrapper - it lives inside the
            // double-quoted -Command - and [string][char]27 rather than [char]27 first, because
            // char + multi-char string throws under PowerShell's char arithmetic.
            const string wrap =
                "$script:KSCwdInner = $function:prompt; " +
                "function prompt { $q = & $script:KSCwdInner; $l = Get-Location; " +
                "if ($l.Provider.Name -eq 'FileSystem') " +
                "{ $q = [string][char]27 + ']9;9;' + $l.ProviderPath + [char]7 + $q }; $q }";

            return " -NoExit -Command \". '" + safe + "'; " + wrap + tail + "\"";
        }

    }
}
