# KillerScan speed-test endpoint

Cloudflare Worker serving KillerScan speed tests at `https://speed.killerscan.net/`. The app uses this endpoint automatically. Workers.dev and preview URLs remain disabled. Account identifiers and deployment credentials are not part of the source or app.

## Deployment verification

September 8, 2026: deployed to Workers Free. Health, zero-byte latency, exact 8 MiB downloads, exact 4 MiB upload acknowledgments, and identity/no-store headers passed. After an initial client download timeout, two consecutive full native-engine runs passed all 26 regression checks, with both measured phases completing eight seconds. Results were 373.04/25.71 Mbps and 386.37/27.42 Mbps (download/upload). The 18 Worker tests also passed. These runs validate this connection and deployment, not reliability across all networks or production load. The initial timeout was not reproduced or assigned a confirmed cause.

## Protocol

| Request | Response |
| --- | --- |
| `GET /health` | JSON protocol name `killerscan-speedtest`, version `1`, byte limits, chunk size and upload deadline. |
| `GET /__down?bytes=N&nonce=UNIQUE` | Fresh random binary data. Default and maximum: 8 MiB. `bytes=0` returns an empty 200 response for latency measurements. |
| `POST /__up` | Counts the raw request stream and returns JSON `{ "bytes": N }`. Maximum: 4 MiB, with a 15-second total upload deadline. |

The client supplies a fresh nonce for each download and measures bytes actually transferred. Responses carry `no-store`, `no-transform`, `Content-Encoding: identity`, and the protocol header `X-KillerScan-SpeedTest: 1`. Downloads use `application/octet-stream`, fresh random chunks no larger than 64 KiB, and backpressure. They do not buffer the full response or claim a stream Content-Length. Workers uses chunked encoding for ordinary streams; `encodeBody: "manual"` avoids automatic encoding. See [Streams](https://developers.cloudflare.com/workers/runtime-apis/streams/) and [Response](https://developers.cloudflare.com/workers/runtime-apis/response/).

Uploads accept uncompressed binary content, validate a supplied Content-Length, discard chunks after counting, and stop on overflow. Invalid input returns 400, wrong methods 405 with Allow, timeout 408, oversized payloads 413, and unsupported encoding/type 415. No browser CORS access is enabled. No speed results or payloads are stored, and no R2 bucket, database, upstream fetch, or executable download is needed. Cloudflare still handles request metadata and temporary rate counters.

## Local verification

Use Node.js 22 or newer from this directory:

```powershell
node --test worker.test.mjs
```

These tests exercise the Worker through Node Request/Response streams, including exact transfer counts, random payloads, cancellation, bounds, deadlines and failed rate counters. They do not reproduce Cloudflare edge compression or production traffic.

For a real HTTP exchange with the compiled net48 engine, start the loopback-only Node adapter in one terminal, then run the read-only check in Windows PowerShell from another:

```powershell
node loopback.mjs
```

```powershell
powershell.exe -NoProfile -File ./engine-loopback.ps1
```

The check loads `bin/Debug/net48/KillerScan.exe` without opening the app and transfers up to 32 MiB per direction. Its parameters can select another compiled app or the production defaults: `-BudgetMiB 512 -PhaseSeconds 8 -WarmupSeconds 2`. The adapter's local-only `/__adapter_stats` reports request counts and statuses; it is not part of the deployed Worker. Stop the adapter with Ctrl+C. Local throughput is a compatibility check, not an internet speed result. The Node adapter does not emulate Cloudflare rate counters or edge encoding.

For a local Workers runtime, use Wrangler 4.36.0 or newer:

```powershell
npx wrangler dev --env local --ip 127.0.0.1
```

The `local` environment sets `LOCAL_TEST=1`. The bypass works only for localhost or loopback request URLs. Never deploy that environment. Production fails closed with 503 if either limiter binding, its boolean result, or the Cloudflare client-IP header is missing. Counter errors also return 503; exhausted limits return 429 and Retry-After.

## Production configuration review

For deployments to another account:

1. Authenticate locally with Wrangler and select the intended account. The configuration uses the plan's built-in CPU limit and supports deployment on Workers Free. Verify sustained transfers against the deployed service before distributing a client.
2. Confirm that rate-limit namespace IDs `715201` and `715202` are unused in that account or replace them with two reviewed, unique positive integer strings. Both bindings are required. The configured limits are 600 requests per minute per client IP and 3,000 requests per minute for the service in each Cloudflare location.
3. Review shared-office/NAT behavior and expected tests per minute. A fast run can use roughly 272 requests, with the exact count affected by adaptive payloads and latency probes. The 600-request client allowance accommodates a complete default run; the 3,000-request location allowance leaves room for five such client allowances. A 1,000-request location allowance would fit fewer than two full client allowances and constrain unrelated simultaneous tests sooner. Multiple users behind one IP still share its allowance. The public endpoint has no user authentication. Do not treat client IP as a unique person.
4. Replace the Custom Domain with a hostname you control. Keep workers.dev and preview URLs disabled, and leave `LOCAL_TEST` absent from production. See [Custom Domains](https://developers.cloudflare.com/workers/configuration/routing/custom-domains/) and [Wrangler configuration](https://developers.cloudflare.com/workers/wrangler/configuration/).
5. Verify the deployed health response, zero-byte latency, exact random download sizes, upload counts, compression headers, cancellation, and 429 behavior from the app before configuring its endpoint. Measure throughput against the intended audience's locations. An edge test measures the route to Cloudflare, not every internet destination.

The binding counters are permissive, eventually consistent, and local to each Cloudflare location. The service limit is not a global rate limit or a hard spending cap. Binding namespace reuse shares counters, including across Workers. These constraints and the configuration syntax are documented in [Rate Limiting](https://developers.cloudflare.com/workers/runtime-apis/bindings/rate-limit/).

## Cost and limits

Verified against official documentation on September 8, 2026: Workers Paid starts at $5 per account per month, including 10 million requests and 30 million CPU milliseconds. Additional requests cost $0.30 per million and CPU time $0.02 per million milliseconds. Workers lists no additional egress or bandwidth charge. Free includes 100,000 requests per day and only 10 ms CPU per invocation. See [Workers pricing](https://developers.cloudflare.com/workers/platform/pricing/).

Workers has a 128 MB isolate memory limit. The endpoint's byte limits are intentionally much lower than Cloudflare's HTTP body limits. Streaming bounds application buffers but does not replace rate controls, CPU caps, billing review or production load verification. See [Workers limits](https://developers.cloudflare.com/workers/platform/limits/).
