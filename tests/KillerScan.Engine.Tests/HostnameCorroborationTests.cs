using KillerScan.Engine;

// A reverse DNS name without a matching forward record is only trusted when the device's own
// mDNS or NetBIOS name says the same thing, so a stale DHCP PTR still cannot name a device.
internal static class HostnameCorroborationTests
{
    internal static Task Run()
    {
        Require(NetworkScanner.PtrMatchesDeviceName("XC-C12789", "XCc12789", ""),
            "A printer PTR matches its own mDNS name despite case and the hyphen.");
        Require(NetworkScanner.PtrMatchesDeviceName("XC-C12789.corp.example", "", "XC-C12789"),
            "A fully qualified PTR matches the NetBIOS name by its first label.");
        Require(!NetworkScanner.PtrMatchesDeviceName("OLD-LAPTOP", "XCc12789", "XCC12789"),
            "A stale PTR for another machine is still rejected.");
        Require(!NetworkScanner.PtrMatchesDeviceName("XC-C12789", "", ""),
            "A PTR with nothing to corroborate it is still rejected.");
        Require(!NetworkScanner.PtrMatchesDeviceName("", "XCc12789", ""),
            "An empty PTR never matches.");
        Console.WriteLine("PASS: an unconfirmed PTR names a device only when the device agrees.");
        return Task.CompletedTask;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
