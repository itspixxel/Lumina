# Lumina share server

The server behind Lumina's **Get link** button: a small [Cloudflare Worker](https://developers.cloudflare.com/workers/) that keeps uploaded screenshots and recordings in [R2](https://developers.cloudflare.com/r2/) for a limited time and serves them from short links.

Lumina uses its own copy by default. You only need this guide if you'd rather your captures went to a server you run. Cloudflare's free tier is plenty for one person or a small team (10 GB of storage, no charge for downloads).

## What it does

- Accepts PNG, JPEG, GIF and MP4 files up to 100 MB, and checks that each file really is what it claims to be.
- Gives every upload a short, random link (`https://your-server/Xk3a9QpZ`) that expires after the time picked in Lumina (1 hour, 1 day or 1 week).
- Lets Lumina delete a link early with a key only the uploader has.
- Removes expired files every hour, with an R2 lifecycle rule as a backstop.
- Limits each address to 20 uploads a minute.

## Set up your own

You need [Node.js](https://nodejs.org/) and a free Cloudflare account with R2 turned on (Cloudflare dashboard, *R2 Object Storage*, then follow the prompts).

```bash
cd share-server
npm install
npx wrangler login
npx wrangler r2 bucket create lumina-share
npx wrangler r2 bucket lifecycle add lumina-share expire-all --expire-days 8 --force
```

Make it private, so only you can upload (recommended), by choosing a long random token:

```bash
npx wrangler secret put UPLOAD_TOKEN
```

Then publish it:

```bash
npx wrangler deploy
```

Wrangler prints the address, something like `https://lumina-share.yourname.workers.dev`. In Lumina, open **Settings**, go to **Sharing**, set *Where files go* to **My own server**, and paste the address and the token.

### Optional

- **Your own domain:** add a custom domain to the Worker in the Cloudflare dashboard (*Workers & Pages*, *lumina-share*, *Settings*, *Domains & Routes*), then set `PUBLIC_URL` in `wrangler.toml` to it and deploy again.
- **Admin token:** `npx wrangler secret put ADMIN_TOKEN` lets you remove any file, for example one that was reported:

  ```bash
  curl -X DELETE https://your-server/api/files/<id> -H "X-Delete-Key: <admin token>"
  ```

- **Abuse contact:** set `ABUSE_CONTACT` in `wrangler.toml` to show an email address on the server's home page.
- **Limits:** `MAX_UPLOAD_MB` and `MAX_EXPIRY_HOURS` in `wrangler.toml`. Cloudflare's free plan accepts request bodies up to 100 MB.

## Developing

```bash
npm run dev     # local server on http://127.0.0.1:8787, with a local R2 bucket
npm test        # runs test/smoke.mjs against it
```

## API

| Request | Does |
| --- | --- |
| `POST /api/upload` | The file as the raw body. Headers: `Content-Type` (`image/png`, `image/jpeg`, `image/gif` or `video/mp4`), `X-File-Name` (URL-encoded), `X-Expires` (seconds), and `Authorization: Bearer <token>` when `UPLOAD_TOKEN` is set. Replies `201` with `{ id, url, deleteKey, expiresAt }`. |
| `DELETE /api/files/:id` | Header `X-Delete-Key` with the upload's `deleteKey` (or `ADMIN_TOKEN`). |
| `GET /:id` | The file, with range support so videos can seek. Expired or deleted links answer `404`. |

Errors come back as `{ "error": "..." }` with a message Lumina shows as it is.
