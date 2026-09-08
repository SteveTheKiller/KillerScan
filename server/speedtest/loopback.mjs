// Local verification adapter. It never listens on an external interface.
import { createServer } from "node:http";
import { Readable } from "node:stream";
import { pipeline } from "node:stream/promises";
import worker from "./worker.mjs";

const argument = process.argv[2] ?? "18765";
if (!/^[0-9]+$/.test(argument) || Number(argument) < 1 || Number(argument) > 65535) {
  throw new Error("Usage: node loopback.mjs [port from 1 through 65535]");
}
const port = Number(argument);
const origin = "http://127.0.0.1:" + port;
const stats = { requests: 0, downloadRequests: 0, latencyRequests: 0, uploadRequests: 0, statuses: {} };
const server = createServer(async (incoming, outgoing) => {
  const url = new URL(incoming.url, origin);
  if (url.pathname === "/__adapter_stats" && incoming.method === "GET") {
    outgoing.writeHead(200, { "Content-Type": "application/json", "Cache-Control": "no-store" });
    outgoing.end(JSON.stringify(stats));
    return;
  }
  stats.requests++;
  if (url.pathname === "/__up") stats.uploadRequests++;
  if (url.pathname === "/__down") {
    if (url.searchParams.get("bytes") === "0") stats.latencyRequests++;
    else stats.downloadRequests++;
  }
  const abort = new AbortController();
  outgoing.on("close", () => { if (!outgoing.writableFinished) abort.abort(); });
  try {
    const init = { method: incoming.method, headers: incoming.headers, signal: abort.signal };
    if (incoming.method !== "GET" && incoming.method !== "HEAD") {
      init.body = Readable.toWeb(incoming);
      init.duplex = "half";
    }
    const response = await worker.fetch(new Request(url, init), { LOCAL_TEST: "1" });
    stats.statuses[response.status] = (stats.statuses[response.status] ?? 0) + 1;
    outgoing.writeHead(response.status, Object.fromEntries(response.headers));
    if (response.body) await pipeline(Readable.fromWeb(response.body), outgoing, { signal: abort.signal });
    else outgoing.end();
  } catch (error) {
    if (abort.signal.aborted || incoming.destroyed || outgoing.destroyed) return;
    if (!outgoing.headersSent) outgoing.writeHead(500, { "Content-Type": "text/plain" });
    outgoing.end("Local adapter failed.");
    console.error(error);
  }
});
server.listen(port, "127.0.0.1", () => console.log("Worker loopback adapter: " + origin));
function stop() {
  console.log(JSON.stringify(stats));
  server.close();
  server.closeAllConnections();
}
process.on("SIGINT", stop);
process.on("SIGTERM", stop);
