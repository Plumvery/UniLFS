# Setting up Cloudflare R2 (or any S3-compatible storage)

Cloudflare R2 is the recommended backend: the free tier includes 10 GB-month of storage, free egress, and generous operation quotas (1M class A + 10M class B operations per month as of this writing). The same steps apply to Amazon S3, MinIO, Wasabi and other S3-compatible services — only the endpoint/region differ.

## 1. Create a bucket

1. Sign in to the [Cloudflare dashboard](https://dash.cloudflare.com/) → **R2 Object Storage**.
2. **Create bucket**. Pick any name, e.g. `my-game-unilfs`. Leave it **private** (default) — UniLFS signs every request, no public access is needed.

## 2. Create an API token

1. In R2, open **Manage R2 API Tokens** → **Create API token**.
2. Permissions: **Object Read & Write**.
3. Scope: *Apply to specific buckets only* → select your bucket.
4. Create, then copy the **Access Key ID** and **Secret Access Key** (shown only once).

Give each team member their own token so tokens can be revoked individually.

## 3. Find your endpoint

The S3 API endpoint is:

```
https://<account-id>.r2.cloudflarestorage.com
```

Your account ID is shown on the R2 overview page (and inside the token creation screen). Use the plain account endpoint — not a custom/public domain.

## 4. Configure Unity

`Edit > Project Settings > UniLFS`:

| Field | Value |
|-------|-------|
| Provider | S3 compatible |
| Endpoint | `https://<account-id>.r2.cloudflarestorage.com` |
| Bucket | `my-game-unilfs` |
| Region | `auto` (R2), or the bucket region for AWS (e.g. `us-east-1`) |
| Key Prefix | `unilfs` (default; lets one bucket serve several projects with different prefixes) |
| Access Key ID / Secret Access Key | from step 2 — stored per-user in `UserSettings/UniLFS.json`, never committed |

Press **Test Connection**. You should see "Connected to S3 (...)". A 403 usually means wrong keys, missing bucket permission, or a wrong system clock.

## Other S3-compatible services

| Service | Endpoint example | Region |
|---------|------------------|--------|
| Amazon S3 | `https://s3.us-east-1.amazonaws.com` | `us-east-1` |
| MinIO (self-hosted) | `https://minio.example.com` | `us-east-1` (or your configured region) |
| Wasabi | `https://s3.ap-northeast-1.wasabisys.com` | `ap-northeast-1` |

UniLFS uses path-style URLs (`endpoint/bucket/key`), which all of the above support.

## Notes

- Objects are stored as `<prefix>/objects/<first-2-hash-chars>/<sha256>`. Identical files are deduplicated automatically.
- Uploads are single PUT requests, so the per-object limit is ~5 GB on R2/S3.
- For CI, set `UNILFS_S3_ACCESS_KEY_ID` / `UNILFS_S3_SECRET_ACCESS_KEY` as secrets — see [ci.md](ci.md).
