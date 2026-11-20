// Quick end-to-end check against a running server: npm run dev, then npm test (or pass a URL as the argument).
const base = (process.argv[2] || "http://127.0.0.1:8787").replace(/\/+$/, "");
const token = process.env.UPLOAD_TOKEN;
let failures = 0;

function check(name, ok, detail = "") {
  console.log(`${ok ? "ok  " : "FAIL"} ${name}${detail ? ` (${detail})` : ""}`);
  if (!ok) failures++;
}

async function upload(bytes, type, expires = 3600) {
  const headers = { "Content-Type": type, "X-File-Name": encodeURIComponent("smoke test.png"), "X-Expires": String(expires) };
  if (token) headers.Authorization = `Bearer ${token}`;
  return fetch(`${base}/api/upload`, { method: "POST", headers, body: bytes });
}

// A 1x1 transparent PNG.
const png = Buffer.from("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=", "base64");

const res = await upload(png, "image/png");
const body = await res.json();
check("upload returns 201", res.status === 201, `${res.status} ${JSON.stringify(body)}`);
check("upload returns a short id", /^[A-Za-z0-9]{8}$/.test(body.id || ""));

const got = await fetch(body.url);
const bytes = Buffer.from(await got.arrayBuffer());
check("file is served", got.status === 200 && bytes.equals(png), `${got.status}`);
check("content type kept", got.headers.get("content-type") === "image/png");
check("nosniff set", got.headers.get("x-content-type-options") === "nosniff");

const part = await fetch(body.url, { headers: { Range: "bytes=0-7" } });
check("range request gives 206", part.status === 206 && (await part.arrayBuffer()).byteLength === 8, `${part.status}`);

const liar = await upload(Buffer.from("<html><script>alert(1)</script></html>"), "image/png");
check("non-image claiming PNG is refused", liar.status === 415, `${liar.status}`);

const html = await upload(png, "text/html");
check("other types are refused", html.status === 415, `${html.status}`);

const wrongKey = await fetch(`${base}/api/files/${body.id}`, { method: "DELETE", headers: { "X-Delete-Key": "nope" } });
check("wrong delete key refused", wrongKey.status === 403, `${wrongKey.status}`);

const del = await fetch(`${base}/api/files/${body.id}`, { method: "DELETE", headers: { "X-Delete-Key": body.deleteKey } });
check("delete with key works", del.status === 200, `${del.status}`);

const gone = await fetch(body.url);
check("deleted link is 404", gone.status === 404, `${gone.status}`);

const short = await upload(png, "image/png", 60);
const shortBody = await short.json();
check("short expiry accepted", short.status === 201 && Date.parse(shortBody.expiresAt) - Date.now() <= 61000);

process.exit(failures ? 1 : 0);
