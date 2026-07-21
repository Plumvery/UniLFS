# Setting up Google Drive

Google Drive works well for solo developers and small teams who already have Drive storage (15 GB free per account). For larger teams, prefer an S3-compatible provider — Drive has per-user API rate limits and OAuth setup is more involved.

UniLFS talks to the Drive API as **your own OAuth app**, so you first create a (free) Google Cloud project.

## 1. Create a Google Cloud project & enable the Drive API

1. Open the [Google Cloud console](https://console.cloud.google.com/) → create a project (any name, e.g. `unilfs`).
2. **APIs & Services → Library** → search **Google Drive API** → **Enable**.

## 2. Configure the OAuth consent screen

1. **APIs & Services → OAuth consent screen**.
2. User type: **External** (unless your whole team is in one Google Workspace — then **Internal** is simpler and skips the warnings below).
3. Fill in the app name and contact emails. Scopes can be left empty (UniLFS requests the Drive scope at sign-in).
4. **Important — refresh token lifetime**: while the consent screen is in **Testing** status, refresh tokens expire after **7 days**, forcing everyone to sign in again weekly. Press **Publish app** to switch to *In production*. You do **not** need Google verification for personal/team use — users just see an "unverified app" warning once during sign-in (`Advanced > Go to ... (unsafe)`).
5. If you stay in Testing mode anyway, add every team member under **Test users**.

## 3. Create an OAuth client

1. **APIs & Services → Credentials → Create Credentials → OAuth client ID**.
2. Application type: **Desktop app**.
3. Copy the **Client ID** and **Client Secret**.

> Google [documents](https://developers.google.com/identity/protocols/oauth2#installed) that for installed/desktop apps the client secret is *not* treated as confidential. It is still good hygiene to keep it out of **public** repos: in that case leave the project-level fields empty and have each member paste the values into the per-user fields.

## 4. Configure Unity & sign in

`Edit > Project Settings > UniLFS`:

1. Provider: **Google Drive**.
2. Paste Client ID / Client Secret (project-level for private repos, per-user for public repos; `UNILFS_DRIVE_CLIENT_ID` / `UNILFS_DRIVE_CLIENT_SECRET` also work).
3. Press **Sign in with Google**. A browser opens; finish the consent screen (including the unverified-app warning if shown). The refresh token is stored per-user in `UserSettings/UniLFS.json` (gitignored).

## 5. Pick a storage folder

Either:

- Press **Create Folder** in the settings (creates it in your My Drive and fills in the ID), then share that folder with your teammates (*Editor* permission), **or**
- Create/share a folder in the Drive UI yourself and paste its ID — the part after `/folders/` in the URL:
  `https://drive.google.com/drive/folders/`**`1AbCdEfGhIjKlMnOpQrStUv`**

Folders on **shared drives** also work (UniLFS passes `supportsAllDrives`).

Press **Test Connection** — you should see the folder's name.

## Notes & caveats

- Files uploaded to a shared *My Drive* folder count against the **uploader's** storage quota and are owned by the uploader. Use a shared drive (Workspace) if you want pooled ownership.
- Drive API rate limits are per-user and modest; UniLFS uses up to `Parallel Transfers` concurrent requests (lower it if you hit 403 rate-limit errors).
- Every team member signs in with their **own** Google account; they only need access to the shared folder.
- **Sign out** in the settings removes the local token. To fully revoke access, also visit [myaccount.google.com/permissions](https://myaccount.google.com/permissions).
- For CI, mint the refresh token once on a developer machine and set it as `UNILFS_DRIVE_REFRESH_TOKEN` (with client ID/secret) — see [ci.md](ci.md).
