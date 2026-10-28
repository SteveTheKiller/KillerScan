# Speed test

The default profile compares two, four, and eight concurrent HTTP transfers during nine seconds of warmup per direction, then runs a 15-second test. Upload keeps the connection count that improved measured throughput by at least 5%. Download always measures with all eight connections, because short warmup bursts do not predict sustained throughput. Request sizes adapt to the connection while staying within the deployed server limits. A 3 GiB budget per direction includes warmup and can shorten a very fast test; the terminal reports that limit. Final throughput uses payload bytes transferred during the timed phase. Upload bytes are counted as they are sent, but the test still requires a valid server acknowledgment. Server rate limits stop the test instead of adding retries to the timing window.

The upload count includes payload successfully written to the HTTP transport before cutoff, including requests still in flight. Some of those bytes can remain buffered before the receiver reads them. A valid acknowledgment establishes that the endpoint completed an upload exchange; later sent bytes remain part of the rate even if their request is unacknowledged. Received and acknowledged totals are separate quantities.

Latency is HTTP round-trip time to the selected endpoint, including server processing. Idle latency is the median of five samples after an unmeasured connection warmup. Jitter is the mean absolute difference between consecutive samples. Loaded latency is sampled on a separate connection during each measured phase. These measurements describe that route and server, and do not claim packet loss or universal ISP capacity.

When you start a speed test, the app connects to `https://speed.killerscan.net/`, the dedicated [KillerScan endpoint](../server/speedtest/README.md) hosted on Cloudflare Workers. Users do not need an account or server setup. Deployment credentials are kept outside the source and app.

Run the retained tests on Windows with the .NET SDK and Node.js 22 or newer:

```powershell
dotnet build tests/SpeedTest.Tests/SpeedTest.Tests.csproj -c Release
./tests/SpeedTest.Tests/bin/Release/net48/SpeedTest.Tests.exe --worker
node --test server/speedtest/worker.test.mjs
```

The release script also adds `--internet` to verify the deployed KillerScan service before publishing or signing. That check uses the app's full budget of up to 3 GiB in each direction, including warmup. The normal checks above stay on loopback; passing them alone does not verify the deployed service.
