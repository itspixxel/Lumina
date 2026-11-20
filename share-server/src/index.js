// Lumina share server: a Cloudflare Worker that keeps uploaded screenshots and recordings in R2 for a limited
// time and serves them from short links like https://share.example.com/Xk3a9QpZ.
//
//   POST   /api/upload       raw file body; returns { id, url, deleteKey, expiresAt }
//   DELETE /api/files/:id    with X-Delete-Key (the upload's key, or ADMIN_TOKEN)
//   GET    /:id             the file itself (range requests supported, so videos can seek)
//
// Files expire on their own: an expired link answers 404 straight away, an hourly cron removes the objects,
// and an R2 lifecycle rule (see README.md) is the backstop.

const TYPES = {
  "image/png": { ext: "png", magic: [[0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]] },
  "image/jpeg": { ext: "jpg", magic: [[0xff, 0xd8, 0xff]] },
  "image/gif": { ext: "gif", magic: [[0x47, 0x49, 0x46, 0x38]] },
  // MP4: "ftyp" at offset 4.
  "video/mp4": { ext: "mp4", magic: [[null, null, null, null, 0x66, 0x74, 0x79, 0x70]] },
};

const ID_ALPHABET = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
const ID_LENGTH = 8;

export default {
  async fetch(request, env, ctx) {
    try {
      return await route(request, env);
    } catch (err) {
      console.error(err);
      return json({ error: "Something went wrong on the server." }, 500);
    }
  },

  async scheduled(event, env, ctx) {
    ctx.waitUntil(removeExpired(env));
  },
};

async function route(request, env) {
  const url = new URL(request.url);
  const path = url.pathname;

  if (path === "/api/upload") {
    if (request.method === "OPTIONS") return new Response(null, { status: 204 });
    if (request.method !== "POST") return json({ error: "Use POST." }, 405);
    return upload(request, env, url);
  }

  const del = path.match(/^\/api\/files\/([A-Za-z0-9]+)$/);
  if (del) {
    if (request.method !== "DELETE") return json({ error: "Use DELETE." }, 405);
    return remove(request, env, del[1]);
  }

  if (path === "/" || path === "") return home(env);

  const file = path.match(/^\/([A-Za-z0-9]{4,32})(?:\.[a-z0-9]{2,4})?$/);
  if (file && (request.method === "GET" || request.method === "HEAD")) return serve(request, env, file[1]);

  return notFound();
}

// ---------- Uploading ----------

async function upload(request, env, url) {
  if (env.UPLOAD_TOKEN) {
    const auth = request.headers.get("Authorization") || "";
    if (!timingSafeEqual(auth, `Bearer ${env.UPLOAD_TOKEN}`)) return json({ error: "This server needs a valid token." }, 401);
  }

  if (env.UPLOAD_LIMITER) {
    const ip = request.headers.get("CF-Connecting-IP") || "unknown";
    const { success } = await env.UPLOAD_LIMITER.limit({ key: ip });
    if (!success) return json({ error: "Too many uploads. Try again in a minute." }, 429);
  }

  const type = (request.headers.get("Content-Type") || "").split(";")[0].trim().toLowerCase();
  if (!TYPES[type]) return json({ error: "Only PNG, JPEG, GIF and MP4 files can be shared." }, 415);

  const length = Number(request.headers.get("Content-Length"));
  const maxBytes = Number(env.MAX_UPLOAD_MB || 100) * 1024 * 1024;
  if (!Number.isFinite(length) || length <= 0) return json({ error: "The upload needs a Content-Length." }, 411);
  if (length > maxBytes) return json({ error: `Files can be up to ${env.MAX_UPLOAD_MB || 100} MB.`, maxBytes }, 413);
  if (!request.body) return json({ error: "The upload was empty." }, 400);

  const maxSeconds = Number(env.MAX_EXPIRY_HOURS || 168) * 3600;
  let seconds = Number(request.headers.get("X-Expires") || 86400);
  if (!Number.isFinite(seconds) || seconds <= 0) seconds = 86400;
  seconds = Math.min(Math.max(Math.round(seconds), 60), maxSeconds);
  const expiresAt = Date.now() + seconds * 1000;

  const body = await checkedBody(request.body, length, TYPES[type].magic);
  if (!body) return json({ error: "The file doesn't look like the type it claims to be." }, 415);

  const id = await freshId(env);
  const deleteKey = randomString(32);
  const name = cleanName(request.headers.get("X-File-Name"), TYPES[type].ext);

  await env.BUCKET.put(id, body, {
    httpMetadata: { contentType: type },
    customMetadata: { expires: String(expiresAt), name, deleteHash: await sha256(deleteKey) },
  });

  const origin = env.PUBLIC_URL ? env.PUBLIC_URL.replace(/\/+$/, "") : url.origin;
  return json({ id, url: `${origin}/${id}`, deleteKey, expiresAt: new Date(expiresAt).toISOString(), maxBytes }, 201);
}

/** Checks the first bytes against the claimed type, then streams the rest to R2 with its length known up front. */
async function checkedBody(body, length, magics) {
  const reader = body.getReader();
  const chunks = [];
  let have = 0;
  while (have < 12) {
    const { done, value } = await reader.read();
    if (done) break;
    chunks.push(value);
    have += value.byteLength;
  }
  const head = new Uint8Array(Math.min(have, 12));
  let offset = 0;
  for (const chunk of chunks) {
    const take = Math.min(chunk.byteLength, head.length - offset);
    head.set(chunk.subarray(0, take), offset);
    offset += take;
    if (offset >= head.length) break;
  }
  const ok = magics.some((magic) => magic.length <= head.length && magic.every((b, i) => b === null || head[i] === b));
  if (!ok) {
    await reader.cancel();
    return null;
  }

  const { readable, writable } = new FixedLengthStream(length);
  const writer = writable.getWriter();
  (async () => {
    try {
      for (const chunk of chunks) await writer.write(chunk);
      for (;;) {
        const { done, value } = await reader.read();
        if (done) break;
        await writer.write(value);
      }
      await writer.close();
    } catch (err) {
      await writer.abort(err).catch(() => {});
    }
  })();
  return readable;
}

async function freshId(env) {
  for (let i = 0; i < 5; i++) {
    const id = randomString(ID_LENGTH);
    if (!(await env.BUCKET.head(id))) return id;
  }
  throw new Error("Couldn't find a free id");
}

function cleanName(raw, ext) {
  let name = "";
  try {
    name = decodeURIComponent(raw || "");
  } catch {
    name = "";
  }
  name = name.replace(/[^\w .()-]/g, "").trim().slice(0, 100);
  if (!name) name = `lumina.${ext}`;
  return name;
}

// ---------- Deleting ----------

async function remove(request, env, id) {
  const key = request.headers.get("X-Delete-Key") || "";
  const object = await env.BUCKET.head(id);
  if (!object) return json({ deleted: true }); // already gone, which is what the caller wanted

  const admin = env.ADMIN_TOKEN && timingSafeEqual(key, env.ADMIN_TOKEN);
  const owner = key && timingSafeEqual(await sha256(key), object.customMetadata?.deleteHash || "");
  if (!admin && !owner) return json({ error: "That delete key doesn't match." }, 403);

  await env.BUCKET.delete(id);
  return json({ deleted: true });
}

async function removeExpired(env) {
  const now = Date.now();
  let cursor;
  let removed = 0;
  do {
    const page = await env.BUCKET.list({ cursor, limit: 1000, include: ["customMetadata"] });
    const expired = page.objects.filter((o) => Number(o.customMetadata?.expires || 0) <= now).map((o) => o.key);
    if (expired.length) {
      await env.BUCKET.delete(expired);
      removed += expired.length;
    }
    cursor = page.truncated ? page.cursor : undefined;
  } while (cursor);
  if (removed) console.log(`Removed ${removed} expired file(s)`);
}

// ---------- Serving ----------

async function serve(request, env, id) {
  const object = await env.BUCKET.get(id, { range: request.headers, onlyIf: request.headers });
  if (!object) return notFound();

  const expires = Number(object.customMetadata?.expires || 0);
  if (expires <= Date.now()) return notFound();

  const headers = new Headers();
  object.writeHttpMetadata(headers);
  headers.set("ETag", object.httpEtag);
  headers.set("Accept-Ranges", "bytes");
  const name = object.customMetadata?.name || id;
  headers.set("Content-Disposition", `inline; filename="${name.replace(/"/g, "")}"`);
  // Keep caches from holding a file past its expiry.
  const remaining = Math.max(0, Math.floor((expires - Date.now()) / 1000));
  headers.set("Cache-Control", `public, max-age=${Math.min(remaining, 3600)}`);
  headers.set("Expires", new Date(expires).toUTCString());
  headers.set("X-Content-Type-Options", "nosniff");
  headers.set("Content-Security-Policy", "default-src 'none'; img-src 'self'; media-src 'self'; style-src 'unsafe-inline'; sandbox");
  headers.set("Referrer-Policy", "no-referrer");
  headers.set("X-Robots-Tag", "noindex");

  // onlyIf failed: R2 hands back the object without a body.
  if (!("body" in object)) return new Response(null, { status: 304, headers });

  let status = 200;
  if (object.range && request.headers.has("Range")) {
    const { offset = 0, length = object.size - offset } = object.range;
    headers.set("Content-Range", `bytes ${offset}-${offset + length - 1}/${object.size}`);
    headers.set("Content-Length", String(length));
    status = 206;
  } else {
    headers.set("Content-Length", String(object.size));
  }
  return new Response(request.method === "HEAD" ? null : object.body, { status, headers });
}

function home(env) {
  const contact = env.ABUSE_CONTACT
    ? `<p>To report a file, write to <a href="mailto:${escapeHtml(env.ABUSE_CONTACT)}">${escapeHtml(env.ABUSE_CONTACT)}</a> with its link.</p>`
    : "";
  return html(
    200,
    "Lumina share",
    `<h1>Lumina share</h1><p>Short-lived links for screenshots and recordings shared from <a href="https://github.com/itspixxel/Lumina">Lumina</a>. Every file here expires on its own.</p>${contact}`
  );
}

function notFound() {
  return html(404, "Link expired", "<h1>This link has expired</h1><p>Files shared with Lumina only last a while, and this one is gone.</p>");
}

// ---------- Helpers ----------

function json(body, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json; charset=utf-8", "Cache-Control": "no-store" },
  });
}

function html(status, title, body) {
  const page = `<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><meta name="robots" content="noindex"><title>${title}</title>
<style>body{margin:0;min-height:100vh;display:grid;place-items:center;background:#0f0f17;color:#e8e8f0;font:16px/1.5 "Segoe UI",system-ui,sans-serif}main{max-width:30rem;padding:2rem}h1{font-size:1.4rem;margin:0 0 .5rem}p{color:#a8a8bc;margin:.5rem 0}a{color:#9d8cff}</style></head><body><main>${body}</main></body></html>`;
  return new Response(page, {
    status,
    headers: { "Content-Type": "text/html; charset=utf-8", "Cache-Control": "no-store", "X-Robots-Tag": "noindex" },
  });
}

function escapeHtml(s) {
  return String(s).replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);
}

function randomString(length) {
  const bytes = crypto.getRandomValues(new Uint8Array(length * 2));
  let out = "";
  for (const b of bytes) {
    // Rejection sampling keeps every character equally likely.
    if (b >= 256 - (256 % ID_ALPHABET.length)) continue;
    out += ID_ALPHABET[b % ID_ALPHABET.length];
    if (out.length === length) return out;
  }
  return out + randomString(length - out.length);
}

async function sha256(text) {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(text));
  return [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, "0")).join("");
}

function timingSafeEqual(a, b) {
  const x = new TextEncoder().encode(a);
  const y = new TextEncoder().encode(b);
  if (x.byteLength !== y.byteLength) return false;
  return crypto.subtle.timingSafeEqual(x, y);
}
