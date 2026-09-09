import test from "node:test";
import assert from "node:assert/strict";
import { deflateSync } from "node:zlib";
import worker, { DOWNLOAD_MAX_BYTES, UPLOAD_MAX_BYTES, CHUNK_BYTES, UPLOAD_TIMEOUT_MS } from "./worker.mjs";

const local = { LOCAL_TEST: "1" };
const allow = () => ({ limit: async () => ({ success: true }) });
const production = () => ({ CLIENT_RATE_LIMITER: allow(), SERVICE_RATE_LIMITER: allow() });
const request = (path, init = {}, publicHost = false) => new Request(
  (publicHost ? "https://speed.killerscan.net" : "http://127.0.0.1:8787") + path, init);
const fetchLocal = (path, init) => worker.fetch(request(path, init), local);

function uploadRequest(body, headers = {}) {
  return request("/__up", { method: "POST", body, duplex: "half", headers });
}

function chunks(total, size = 8192, cancel = () => {}) {
  let remaining = total;
  return new ReadableStream({
    pull(controller) {
      if (!remaining) { controller.close(); return; }
      const length = Math.min(size, remaining);
      remaining -= length;
      controller.enqueue(new Uint8Array(length));
    }, cancel
  }, { highWaterMark: 0 });
}

function assertUncached(response, type = "application/json; charset=utf-8") {
  assert.equal(response.headers.get("Cache-Control"), "no-store, no-transform");
  assert.equal(response.headers.get("CDN-Cache-Control"), "no-store");
  assert.equal(response.headers.get("Cloudflare-CDN-Cache-Control"), "no-store");
  assert.equal(response.headers.get("Content-Encoding"), "identity");
  assert.equal(response.headers.get("Content-Type"), type);
  assert.equal(response.headers.get("X-KillerScan-SpeedTest"), "1");
}

test("health describes the protocol and application limits", async () => {
  const response = await fetchLocal("/health");
  assert.equal(response.status, 200);
  assertUncached(response);
  assert.deepEqual(await response.json(), {
    protocol: "killerscan-speedtest", version: 1,
    downloadMaxBytes: DOWNLOAD_MAX_BYTES, uploadMaxBytes: UPLOAD_MAX_BYTES,
    chunkBytes: CHUNK_BYTES, uploadTimeoutMs: UPLOAD_TIMEOUT_MS
  });
});

test("zero-byte download measures response latency without payload", async () => {
  const response = await fetchLocal("/__down?bytes=0&nonce=latency-1");
  assert.equal(response.status, 200);
  assertUncached(response, "application/octet-stream");
  assert.equal((await response.arrayBuffer()).byteLength, 0);
  assert.equal(response.headers.get("X-SpeedTest-Bytes"), "0");
});

test("default download streams exactly 8 MiB with bounded random generation", async (t) => {
  const original = crypto.getRandomValues.bind(crypto);
  let generated = 0;
  t.mock.method(crypto, "getRandomValues", (buffer) => {
    generated += buffer.byteLength;
    return original(buffer);
  });
  const response = await fetchLocal("/__down?nonce=download-1");
  assert.equal(response.status, 200);
  assertUncached(response, "application/octet-stream");
  assert.equal(response.headers.get("X-SpeedTest-Bytes"), String(DOWNLOAD_MAX_BYTES));
  const reader = response.body.getReader();
  let bytes = 0;
  let chunkCount = 0;
  for (;;) {
    const { value, done } = await reader.read();
    if (done) break;
    assert.ok(value.byteLength > 0 && value.byteLength <= CHUNK_BYTES);
    bytes += value.byteLength;
    chunkCount++;
  }
  assert.equal(bytes, DOWNLOAD_MAX_BYTES);
  assert.equal(chunkCount, DOWNLOAD_MAX_BYTES / CHUNK_BYTES);
  assert.ok(generated <= 2 * CHUNK_BYTES, "Large downloads must not regenerate random bytes for every chunk");
});

test("download is fresh random data and not a compressible repeated pattern", async () => {
  const first = new Uint8Array(await (await fetchLocal("/__down?bytes=131073&nonce=a")).arrayBuffer());
  const second = new Uint8Array(await (await fetchLocal("/__down?bytes=131073&nonce=b")).arrayBuffer());
  assert.equal(first.byteLength, 131073);
  assert.notDeepEqual(first, second);
  assert.notDeepEqual(first.subarray(0, CHUNK_BYTES), first.subarray(CHUNK_BYTES, 2 * CHUNK_BYTES));
  assert.ok(deflateSync(first).byteLength >= first.byteLength * 0.99);
});

test("download cancellation stops the stream", async () => {
  const response = await fetchLocal("/__down");
  const reader = response.body.getReader();
  await reader.read();
  await reader.cancel();
  assert.equal((await reader.read()).done, true);
});

test("invalid download sizes cannot bypass the bound", async () => {
  for (const query of ["bytes=", "bytes=-1", "bytes=1.5", "bytes=1e3", "bytes=NaN", "bytes=00", "bytes=1&bytes=2", "bytes=9007199254740992"]) {
    const response = await fetchLocal("/__down?" + query);
    assert.equal(response.status, 400, query);
    assertUncached(response);
  }
  assert.equal((await fetchLocal("/__down?bytes=" + (DOWNLOAD_MAX_BYTES + 1))).status, 413);
});

test("only the documented methods and paths are served", async () => {
  for (const [path, method, allowed] of [["/__up", "GET", "POST"], ["/__down", "POST", "GET"], ["/health", "HEAD", "GET"], ["/__up", "OPTIONS", "POST"]]) {
    const response = await fetchLocal(path, { method });
    assert.equal(response.status, 405);
    assert.equal(response.headers.get("Allow"), allowed);
    assertUncached(response);
  }
  assert.equal((await fetchLocal("/unknown")).status, 404);
});

test("upload counts a streamed maximum body without requiring Content-Length", async () => {
  const response = await worker.fetch(uploadRequest(chunks(UPLOAD_MAX_BYTES), { "Content-Type": "application/octet-stream" }), local);
  assert.equal(response.status, 200);
  assertUncached(response);
  assert.deepEqual(await response.json(), { bytes: UPLOAD_MAX_BYTES });
});

test("empty and correctly declared uploads return exact counts", async () => {
  for (const size of [0, 1, 65537]) {
    const response = await worker.fetch(uploadRequest(size ? chunks(size) : null, { "Content-Length": String(size) }), local);
    assert.equal(response.status, 200);
    assert.deepEqual(await response.json(), { bytes: size });
  }
});

test("oversized streaming upload is canceled rather than consumed in full", async () => {
  let canceled = false;
  const response = await worker.fetch(uploadRequest(chunks(UPLOAD_MAX_BYTES + CHUNK_BYTES * 10, CHUNK_BYTES, () => { canceled = true; })), local);
  assert.equal(response.status, 413);
  assert.equal(canceled, true);
  assertUncached(response);
});

test("oversized declared upload is rejected before reading the body", async () => {
  let reads = 0;
  const body = new ReadableStream({ pull() { reads++; } }, { highWaterMark: 0 });
  const response = await worker.fetch(uploadRequest(body, { "Content-Length": String(UPLOAD_MAX_BYTES + 1) }), local);
  assert.equal(response.status, 413);
  assert.equal(reads, 0);
});

test("invalid and mismatched Content-Length never returns a successful count", async () => {
  for (const length of ["-1", "1.5", "abc", "9007199254740992", "1, 2"]) {
    assert.equal((await worker.fetch(uploadRequest(null, { "Content-Length": length }), local)).status, 400);
  }
  for (const [actual, declared] of [[0, 1], [1, 2], [2, 1]]) {
    const response = await worker.fetch(uploadRequest(actual ? chunks(actual) : null, { "Content-Length": String(declared) }), local);
    assert.equal(response.status, 400);
  }
});

test("compressed or nonbinary upload types are rejected", async () => {
  for (const headers of [{ "Content-Encoding": "gzip" }, { "Content-Type": "text/plain" }]) {
    assert.equal((await worker.fetch(uploadRequest(new Uint8Array(1), headers), local)).status, 415);
  }
});

test("failed and nonbyte upload streams return errors", async () => {
  for (const body of [new ReadableStream({ start(c) { c.error(new Error("connection lost")); } }), new ReadableStream({ start(c) { c.enqueue("not bytes"); c.close(); } })]) {
    assert.equal((await worker.fetch(uploadRequest(body), local)).status, 400);
  }
});

test("slow uploads have a total deadline", async (t) => {
  t.mock.timers.enable({ apis: ["setTimeout"] });
  let canceled = false;
  const pending = worker.fetch(uploadRequest(new ReadableStream({ cancel() { canceled = true; } })), local);
  await Promise.resolve();
  t.mock.timers.tick(UPLOAD_TIMEOUT_MS);
  const response = await pending;
  assert.equal(response.status, 408);
  assert.equal(canceled, true);
});

test("production is closed when bindings or the trusted client header are absent", async () => {
  for (const env of [{}, { CLIENT_RATE_LIMITER: allow() }, { ...production(), SERVICE_RATE_LIMITER: {} }, production()]) {
    assert.equal((await worker.fetch(request("/health", {}, true), env)).status, 503);
  }
  assert.equal((await worker.fetch(request("/health", {}, true), local)).status, 503);
});

test("rate limiter denial, exceptions and malformed results fail closed", async () => {
  for (const binding of ["CLIENT_RATE_LIMITER", "SERVICE_RATE_LIMITER"]) {
    for (const [limit, status] of [[async () => ({ success: false }), 429], [async () => { throw new Error("unavailable"); }, 503], [async () => ({}), 503], [async () => null, 503]]) {
      const response = await worker.fetch(request("/__down?bytes=0", { headers: { "CF-Connecting-IP": "192.0.2.1" } }, true), { ...production(), [binding]: { limit } });
      assert.equal(response.status, status);
      assert.equal(response.headers.get("Retry-After"), "60");
      assertUncached(response);
    }
  }
});

test("cache-busting parameters do not change rate-limit keys", async () => {
  const clientKeys = [], serviceKeys = [];
  const env = {
    CLIENT_RATE_LIMITER: { limit: async ({ key }) => { clientKeys.push(key); return { success: true }; } },
    SERVICE_RATE_LIMITER: { limit: async ({ key }) => { serviceKeys.push(key); return { success: true }; } }
  };
  for (const nonce of ["one", "two"]) {
    assert.equal((await worker.fetch(request("/__down?bytes=0&nonce=" + nonce, { headers: { "CF-Connecting-IP": "192.0.2.10" } }, true), env)).status, 200);
  }
  assert.deepEqual(clientKeys, ["client:192.0.2.10", "client:192.0.2.10"]);
  assert.deepEqual(serviceKeys, ["speedtest", "speedtest"]);
});
