namespace KillerScan.Engine
{
    /// <summary>The stage a subnet scan has reached.</summary>
    public enum ScanStage
    {
        /// <summary>Ping, TCP liveness and ARP discovery. <see cref="ScanStatus.Label"/> is the target summary.</summary>
        Discovering,
        /// <summary>Resolving MAC addresses. <see cref="ScanStatus.Count"/> is the number of live hosts.</summary>
        ResolvingMacs,
        /// <summary>Full port and fingerprint probes. <see cref="ScanStatus.Count"/> is the number of hosts.</summary>
        Probing,
        /// <summary>Quick scan name and vendor resolution. <see cref="ScanStatus.Count"/> is the number of hosts.</summary>
        ResolvingHosts,
        /// <summary>Finished. <see cref="ScanStatus.Count"/> is the number of devices found.</summary>
        Complete,
    }

    /// <summary>
    /// A scan progress report. The engine reports the stage and its numbers only; the host turns
    /// them into text in the reader's language.
    /// </summary>
    public readonly struct ScanStatus
    {
        public ScanStatus(ScanStage stage, int count = 0, string label = "")
        {
            Stage = stage;
            Count = count;
            Label = label;
        }

        public ScanStage Stage { get; }
        public int Count { get; }
        public string Label { get; }

        /// <summary>English text for hosts without their own strings, such as the CLI and tests.</summary>
        public override string ToString() => Stage switch
        {
            ScanStage.Discovering    => $"Discovering hosts on {Label}...",
            ScanStage.ResolvingMacs  => $"Resolving {Count} MAC addresses...",
            ScanStage.Probing        => $"Probing {Count} alive hosts...",
            ScanStage.ResolvingHosts => $"Resolving {Count} hosts...",
            ScanStage.Complete       => $"Scan complete - {Count} devices found",
            _                        => Stage.ToString(),
        };
    }
}
