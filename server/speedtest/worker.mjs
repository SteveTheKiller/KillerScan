export const DOWNLOAD_MAX_BYTES = 8 * 1024 * 1024;
export const UPLOAD_MAX_BYTES = 4 * 1024 * 1024;
export const CHUNK_BYTES = 64 * 1024;
export const UPLOAD_TIMEOUT_MS = 15_000;

const BASE_HEADERS = {
  "Cache-Control": "no-store, no-transform",
  "CDN-Cache-Control": "no-store",
  "Cloudflare-CDN-Cache-Control": "no-store",
  "Content-Encoding": "identity",
  "X-Content-Type-Options": "nosniff",
  "X-KillerScan-SpeedTest": "1"
};

function json(value, status = 200, extraHeaders = {}) {
  return new Response(JSON.stringify(value), {
    status,
    encodeBody: "manual",
    headers: { ...BASE_HEADERS, "Content-Type": "application/json; charset=utf-8", ...extraHeaders }
  });
}

function error(status, message, headers) {
  return json({ error: message }, status, headers);
}

async function authorize(request, env, url) {
  if (env.LOCAL_TEST === "1") {
    if (["localhost", "127.0.0.1", "[::1]"].includes(url.hostname)) return null;
    return error(503, "Local test mode cannot serve a public host.");
  }
  const client = env.CLIENT_RATE_LIMITER;
  const service = env.SERVICE_RATE_LIMITER;
  const ip = request.headers.get("CF-Connecting-IP");
  if (!client || typeof client.limit !== "function" ||
      !service || typeof service.limit !== "function" || !ip || ip.length > 64) {
    return error(503, "Speed test rate limiting is not configured.");
  }
  try {
    const result = await client.limit({ key: "client:" + ip });
    if (!result || typeof result.success !== "boolean") throw new Error("Invalid counter result");
    if (!result.success) return error(429, "Please wait before testing again.", { "Retry-After": "60" });
    const overall = await service.limit({ key: "speedtest" });
    if (!overall || typeof overall.success !== "boolean") throw new Error("Invalid counter result");
    if (!overall.success) return error(429, "Speed test capacity is temporarily limited.", { "Retry-After": "60" });
    return null;
  } catch {
    return error(503, "Speed test rate limiting is unavailable.", { "Retry-After": "60" });
  }
}

function parseCount(value) {
  if (!/^(0|[1-9][0-9]*)$/.test(value)) return null;
  const count = Number(value);
  return Number.isSafeInteger(count) ? count : null;
}

function download(url) {
  const values = url.searchParams.getAll("bytes");
  if (values.length > 1) return error(400, "Specify bytes once.");
  const count = values.length ? parseCount(values[0]) : DOWNLOAD_MAX_BYTES;
  if (count === null) return error(400, "bytes must be a nonnegative integer.");
  if (count > DOWNLOAD_MAX_BYTES) return error(413, "Download exceeds the 8 MiB limit.");
  let remaining = count;
  const body = count === 0 ? null : new ReadableStream({
    pull(controller) {
      const chunk = new Uint8Array(Math.min(CHUNK_BYTES, remaining));
      crypto.getRandomValues(chunk);
      remaining -= chunk.byteLength;
      controller.enqueue(chunk);
      if (remaining === 0) controller.close();
    },
    cancel() { remaining = 0; }
  }, { highWaterMark: 0 });
  return new Response(body, {
    encodeBody: "manual",
    headers: {
      ...BASE_HEADERS,
      "Content-Type": "application/octet-stream",
      "X-SpeedTest-Bytes": String(count)
    }
  });
}

async function upload(request) {
  const encoding = request.headers.get("Content-Encoding");
  if (encoding && encoding.toLowerCase() !== "identity") {
    return error(415, "Send an uncompressed upload body.");
  }
  const contentType = request.headers.get("Content-Type");
  if (contentType && contentType.split(";", 1)[0].trim().toLowerCase() !== "application/octet-stream") {
    return error(415, "Send application/octet-stream.");
  }
  const length = request.headers.get("Content-Length");
  const declared = length === null ? null : parseCount(length);
  if (length !== null && declared === null) return error(400, "Invalid Content-Length.");
  if (declared !== null && declared > UPLOAD_MAX_BYTES) return error(413, "Upload exceeds the 4 MiB limit.");
  if (!request.body) {
    return declared > 0 ? error(400, "Upload length does not match Content-Length.") : json({ bytes: 0 });
  }
  const reader = request.body.getReader();
  let timer;
  const timeout = new Error("Upload timed out");
  const deadline = new Promise((_, reject) => {
    timer = setTimeout(() => reject(timeout), UPLOAD_TIMEOUT_MS);
  });
  let bytes = 0;
  let complete = false;
  try {
    for (;;) {
      const { value, done } = await Promise.race([reader.read(), deadline]);
      if (done) { complete = true; break; }
      if (!(value instanceof Uint8Array)) return error(400, "Invalid upload data.");
      bytes += value.byteLength;
      if (bytes > UPLOAD_MAX_BYTES) return error(413, "Upload exceeds the 4 MiB limit.");
      if (declared !== null && bytes > declared) return error(400, "Upload length does not match Content-Length.");
    }
    if (declared !== null && bytes !== declared) return error(400, "Upload length does not match Content-Length.");
    return json({ bytes });
  } catch (ex) {
    return ex === timeout ? error(408, "Upload timed out.") : error(400, "Upload was interrupted.");
  } finally {
    clearTimeout(timer);
    if (!complete) {
      try { await reader.cancel(); } catch { /* A failed stream is already closed. */ }
    }
    reader.releaseLock();
  }
}

export default {
  async fetch(request, env = {}) {
    const url = new URL(request.url);
    const expected = url.pathname === "/__up" ? "POST" :
      ["/__down", "/health"].includes(url.pathname) ? "GET" : null;
    if (!expected) return error(404, "Not found.");
    if (request.method !== expected) return error(405, "Method not allowed.", { Allow: expected });
    const denied = await authorize(request, env, url);
    if (denied) return denied;
    if (url.pathname === "/health") {
      return json({
        protocol: "killerscan-speedtest", version: 1,
        downloadMaxBytes: DOWNLOAD_MAX_BYTES, uploadMaxBytes: UPLOAD_MAX_BYTES,
        chunkBytes: CHUNK_BYTES, uploadTimeoutMs: UPLOAD_TIMEOUT_MS
      });
    }
    return url.pathname === "/__down" ? download(url) : upload(request);
  }
};
