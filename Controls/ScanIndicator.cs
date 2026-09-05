namespace KillerScan.Controls
{
    /// <summary>
    /// What the footer light is reporting. Idle covers both "nothing scanned yet" and "a scan was
    /// stopped before it finished", because from the outside those are the same thing: what is on
    /// screen is not a complete picture of the network.
    /// </summary>
    public enum ScanIndicator
    {
        Idle,
        Scanning,
        Deep,
        Complete
    }
}
