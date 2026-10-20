using KillerScan.Engine;

internal sealed class IsolatedDiscovery : IScanDiscovery
{
    public Task<Dictionary<string, MulticastDiscovery.MdnsInfo>> CollectMdnsAsync(CancellationToken ct) =>
        Task.FromResult(new Dictionary<string, MulticastDiscovery.MdnsInfo>());
    public Task<Dictionary<string, string>> CollectSsdpAsync(CancellationToken ct) =>
        Task.FromResult(new Dictionary<string, string>());
}
