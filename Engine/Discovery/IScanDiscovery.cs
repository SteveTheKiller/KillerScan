namespace KillerScan.Engine
{
    /// <summary>Network-wide service discovery transport used during a subnet scan.</summary>
    public interface IScanDiscovery
    {
        Task<Dictionary<string, MulticastDiscovery.MdnsInfo>> CollectMdnsAsync(CancellationToken ct);
        Task<Dictionary<string, string>> CollectSsdpAsync(CancellationToken ct);
    }

    internal sealed class MulticastScanDiscovery : IScanDiscovery
    {
        public Task<Dictionary<string, MulticastDiscovery.MdnsInfo>> CollectMdnsAsync(CancellationToken ct) => MulticastDiscovery.CollectMdnsAsync(ct);
        public Task<Dictionary<string, string>> CollectSsdpAsync(CancellationToken ct) => MulticastDiscovery.CollectSsdpAsync(ct);
    }
}
