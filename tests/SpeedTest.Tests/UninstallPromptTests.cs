using System;
using KillerScan.Services;
using KillerFamily.Deployment;

/// <summary>
/// Uninstalling from Settings while KillerScan is open used to fail in a hidden helper and
/// leave the app installed with no word to the user. The confirmation step now asks them to
/// close it first. The install-conflict repair likewise says why the other copy stayed.
/// </summary>
internal static class UninstallPromptTests
{
    internal static void Run()
    {
        const string image = @"C:\Program Files\KillerScan\KillerScan.exe";
        int asks = 0, closePrompts = 0, probes = 0;

        // Nothing else running: one question, then go.
        Require(UninstallCompletion.ConfirmInteractive(image, _ => 0, () => { asks++; return true; }, () => { closePrompts++; return true; }) == 0
            && asks == 1 && closePrompts == 0, "With no other copy open, the uninstall confirmation is the only question.");

        // Declining the uninstall stops before anything else is checked.
        asks = closePrompts = 0;
        Require(UninstallCompletion.ConfirmInteractive(image, _ => { probes++; return 1; }, () => { asks++; return false; }, () => { closePrompts++; return true; })
            == InstallerExitCodes.Cancelled && closePrompts == 0, "Declining the uninstall cancels it.");

        // A copy is open: the user is asked to close it, and Try again continues once it is gone.
        int open = 2;
        asks = closePrompts = 0;
        Require(UninstallCompletion.ConfirmInteractive(image, _ => open, () => { asks++; return true; }, () => { closePrompts++; open--; return true; }) == 0
            && closePrompts == 2, "An open copy is named before removal, and Try again goes on once every copy is closed.");

        // Cancelling at that point is the busy answer, not a silent failure later.
        closePrompts = 0;
        Require(UninstallCompletion.ConfirmInteractive(image, _ => 1, () => true, () => { closePrompts++; return false; })
            == InstallerExitCodes.Busy && closePrompts == 1, "Cancelling while a copy is open ends the uninstall as busy.");

        // The conflict repair's elevated helper: success says nothing, anything else says why.
        Require(UninstallCompletion.ConflictRemovalMessage(0, key => key) == null, "A removed copy needs no message.");
        Require(UninstallCompletion.ConflictRemovalMessage(InstallerExitCodes.Busy, key => key) == "Str_Conflict_Busy",
            "A copy that is still open says to close it.");
        Require(UninstallCompletion.ConflictRemovalMessage(5, key => key == "Str_Conflict_Failed" ? "failed ({0})" : key) == "failed (5)",
            "Any other failure names its code.");
        Console.WriteLine("PASS: uninstall asks to close an open copy first, and the conflict repair reports a failed removal.");
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
