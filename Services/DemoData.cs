using System;
using System.Collections.Generic;
using KillerScan.Models;

namespace KillerScan.Services
{
    /// <summary>One fabricated network, everything the demo view needs to fill itself in.</summary>
    internal sealed class DemoScan
    {
        /// <summary>The /24 prefix, e.g. "192.168.1".</summary>
        internal string Network = "";
        internal string Subnet  => $"{Network}.0/24";
        internal string Gateway => $"{Network}.1";
        internal string LocalIp = "";
        internal bool   Wireless;
        internal List<NetworkDevice> Devices = [];
    }

    /// <summary>
    /// Screenshot / demo mode. Launch with `KillerScan.exe --demo` (or /demo). The grid is filled
    /// with a fabricated network, and every press of Scan re-rolls a fresh one (new subnet, IPs,
    /// vendors, MACs, types and ports). Only reachable through the launch flag - no button, never
    /// runs during a real scan - so it is safe to leave in a shipped build.
    ///
    /// Everything here is invented. Subnets, devices, vendors and MACs are chosen to NOT match any
    /// real environment.
    /// </summary>
    internal static class DemoData
    {
        /// <summary>Set once from the launch arguments; read by the window and the About card.</summary>
        internal static bool Enabled;

        private static readonly string[] Subnets =
            { "10.0.0", "10.10.10", "172.16.4", "192.168.0", "192.168.1", "192.168.50" };

        // Always the .1 gateway.
        private static readonly (string Vendor, string[] Ouis, string Type, int[] Ports, string[] Names) Gateway =
            ("Ubiquiti Inc.", new[]{"24:5A:4C","78:8A:20"}, "Router", new[]{22,53,80,443}, new[]{"gateway","router","udm"});

        // Pool of devices to draw the rest of the network from. Vendors, types and ports are varied
        // and intentionally different from the author's own kit.
        private static readonly (string Vendor, string[] Ouis, string Type, int[] Ports, string[] Names)[] Pool =
        {
            ("Netgear",                    new[]{"9C:D3:6D","A0:40:A0","3C:37:86"}, "Switch/AP",      new[]{22,80,443,161},            new[]{"switch","ap","orbi"}),
            ("MikroTik",                   new[]{"48:8F:5A","DC:2C:6E","64:D1:54"}, "Router",         new[]{22,23,80,8291},            new[]{"mikrotik","rb","hex"}),
            ("Aruba Networks",             new[]{"00:0B:86","20:4C:03"},            "Switch/AP",      new[]{22,443,161,4343},          new[]{"ap","aruba","iap"}),
            ("QNAP Systems, Inc.",         new[]{"24:5E:BE","00:08:9B"},            "NAS",            new[]{80,139,443,445,8080},      new[]{"nas","qnap","ts"}),
            ("Western Digital Technologies",new[]{"00:90:A9","00:14:EE"},           "NAS",            new[]{80,443,139,445,9000},      new[]{"mycloud","wd","store"}),
            ("Dell Inc.",                  new[]{"18:66:DA","F8:BC:12","B0:83:FE"}, "Hypervisor",     new[]{22,443,902,5989},          new[]{"esxi","vsphere","host"}),
            ("Dell Inc.",                  new[]{"D0:67:E5","84:7B:EB"},            "Windows Server", new[]{53,88,135,139,389,445,3389},new[]{"dc","srv","ad","files"}),
            ("Hewlett Packard",            new[]{"00:1B:78","3C:D9:2B","98:E7:F4"}, "Printer",        new[]{80,443,515,631,9100,161},  Array.Empty<string>()),
            ("Brother Industries, Ltd.",   new[]{"30:05:5C","00:80:77"},            "Printer",        new[]{80,443,515,631,9100},      new[]{"brother","mfc","printer"}),
            ("Hangzhou Hikvision Digital", new[]{"C0:56:E3","44:19:B6","BC:AD:28"}, "Camera",         new[]{80,554,8000,8200},         Array.Empty<string>()),
            ("Dahua Technology",           new[]{"3C:EF:8C","90:02:A9","E0:50:8B"}, "Camera",         new[]{80,443,554,37777},         Array.Empty<string>()),
            ("Axis Communications",        new[]{"00:40:8C","AC:CC:8E","B8:A4:4F"}, "Camera",         new[]{80,443,554},               new[]{"axis","cam"}),
            ("Roku, Inc.",                 new[]{"CC:6D:A0","B8:3E:59","DC:3A:5E"}, "Smart TV",       new[]{8060,8080,9080},           Array.Empty<string>()),
            ("Samsung Electronics",        new[]{"8C:C8:4B","F8:77:B8","54:88:0E"}, "Smart TV",       new[]{8001,8002,9197},           Array.Empty<string>()),
            ("LG Electronics",             new[]{"00:E0:91","C4:36:6C","A8:16:B2"}, "Smart TV",       new[]{3000,3001,9741},           Array.Empty<string>()),
            ("Sonos, Inc.",                new[]{"5C:AA:FD","B8:E9:37","94:9F:3E"}, "Media Streamer", new[]{1400,1443,4070},           Array.Empty<string>()),
            ("Google LLC",                 new[]{"1C:F2:9A","54:60:09","F4:F5:E8"}, "Media Streamer", new[]{8008,8009,8443,9000},      Array.Empty<string>()),
            ("NVIDIA Corporation",         new[]{"00:04:4B","48:B0:2D"},            "Media Streamer", new[]{8080,32400},               new[]{"shield","plex"}),
            ("Apple, Inc.",                new[]{"F0:18:98","3C:06:30","A4:83:E7"}, "iPhone",         new[]{62078},                    Array.Empty<string>()),
            ("Apple, Inc.",                new[]{"90:DD:5D","68:AB:1E"},            "Apple Device",   new[]{7000,49152},               new[]{"appletv","apple-tv"}),
            ("Google LLC",                 new[]{"64:16:66","3C:5A:B4","DA:A1:19"}, "Android",        Array.Empty<int>(),              Array.Empty<string>()),
            ("Amazon Technologies Inc.",   new[]{"44:65:0D","FC:65:DE","68:37:E9"}, "IoT",            Array.Empty<int>(),              Array.Empty<string>()),
            ("Signify Netherlands B.V.",   new[]{"00:17:88","EC:B5:FA"},            "IoT",            new[]{80,443},                   new[]{"hue"}),
            ("Tuya Smart Inc.",            new[]{"D8:1F:12","10:D5:61"},            "IoT",            new[]{6668},                     Array.Empty<string>()),
            ("Raspberry Pi Trading Ltd",   new[]{"B8:27:EB","DC:A6:32","E4:5F:01"}, "Linux/SSH",      new[]{22,80,443},                new[]{"pi","rpi","node"}),
            ("Raspberry Pi Trading Ltd",   new[]{"28:CD:C1","2C:CF:67"},            "Home Assistant", new[]{1883,8123},                new[]{"homeassistant","hass"}),
            ("Raspberry Pi Trading Ltd",   new[]{"B8:27:EB","E4:5F:01"},            "DNS Server",     new[]{22,53,80},                 new[]{"pihole","adguard","dns"}),
            ("Intel Corporate",            new[]{"34:13:E8","3C:FD:FE","A0:88:69"}, "Windows",        new[]{135,139,445,3389},         new[]{"desktop","pc","workstation"}),
            ("VMware, Inc.",               new[]{"00:0C:29","00:50:56"},            "Linux/SSH",      new[]{22,80,443,8080},           new[]{"vm","ubuntu","debian"}),
            ("Zebra Technologies",         new[]{"00:07:4D","84:24:8D"},            "Printer",        new[]{80,515,631,9100},          Array.Empty<string>()),
        };

        /// <summary>Rolls a whole fabricated network: a gateway at .1 plus 16 to 28 other hosts.</summary>
        internal static DemoScan Generate(Random rng)
        {
            string net = Subnets[rng.Next(Subnets.Length)];
            var scan = new DemoScan
            {
                Network  = net,
                LocalIp  = $"{net}.{rng.Next(20, 60)}",
                Wireless = rng.Next(2) == 1,
            };

            var used = new HashSet<int> { 1 };
            scan.Devices.Add(MakeDevice(net, 1, Gateway, rng));

            int count = rng.Next(16, 29);
            for (int i = 0; i < count; i++)
            {
                int host;
                do { host = rng.Next(2, 255); } while (!used.Add(host));
                scan.Devices.Add(MakeDevice(net, host, Pool[rng.Next(Pool.Length)], rng));
            }
            return scan;
        }

        private static NetworkDevice MakeDevice(
            string net, int host,
            (string Vendor, string[] Ouis, string Type, int[] Ports, string[] Names) t, Random rng)
        {
            string oui = t.Ouis[rng.Next(t.Ouis.Length)];
            string mac = $"{oui}:{rng.Next(256):X2}:{rng.Next(256):X2}:{rng.Next(256):X2}";

            var ports = new List<int>();
            foreach (var p in t.Ports)
                if (rng.Next(100) < 75) ports.Add(p);
            if (t.Ports.Length > 0 && ports.Count == 0) ports.Add(t.Ports[0]);

            string name = "";
            if (t.Names.Length > 0 && rng.Next(100) < 80)
            {
                name = t.Names[rng.Next(t.Names.Length)];
                if (rng.Next(100) < 45) name += "-" + rng.Next(1, 30);
                name += ".lan";
            }

            return new NetworkDevice
            {
                IpAddress  = $"{net}.{host}",
                Hostname   = name,
                MacAddress = mac,
                Vendor     = t.Vendor,
                DeviceType = t.Type,
                OpenPorts  = ports
            };
        }
    }
}
