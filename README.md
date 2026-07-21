# UniLFS

Store large Unity assets in **your own external storage** — Cloudflare R2, any S3-compatible service, or Google Drive — instead of Git LFS.

日本語版は [README_JA.md](README_JA.md) をどうぞ。

Git LFS free tiers are tiny (GitHub: 1 GB storage / 1 GB bandwidth per month) and Unity projects blow through them fast. UniLFS keeps your big binaries out of git entirely: git only stores a small manifest with content hashes, and the real files live in storage you control — for example R2's free tier gives you 10 GB with **zero egress fees**.

- **No git-lfs, no CLI tools, no server** — a pure Unity editor package
- **Bring your own storage** — Cloudflare R2 / Amazon S3 / MinIO / Wasabi (S3 API), or Google Drive
- **`.meta` files stay in git** — GUIDs and references never break
- **Content-addressed & verified** — blobs are stored by SHA-256 and every download is hash-checked
- **Merge-friendly manifest** — one line per file, sorted, so PRs stay reviewable
- **CI ready** — batch mode entry points + environment variable credentials

## How it works

```
your-project/
├── unilfs.manifest.json     ← committed to git (small: path + sha256 + size)
├── .gitignore               ← UniLFS maintains a managed block in here
├── Assets/
│   ├── Big/model.fbx        ← gitignored, restored by UniLFS
│   └── Big/model.fbx.meta   ← committed to git as usual
└── UserSettings/UniLFS.json ← your credentials (never committed)

remote storage:
└── unilfs/objects/ab/abcdef1234...   ← blobs named by SHA-256
```

1. **Track** — you pick large files; UniLFS records their SHA-256 in `unilfs.manifest.json` and adds them to a managed `.gitignore` block.
2. **Push** — blobs missing from the remote are uploaded. The manifest only records a new hash after its blob is confirmed uploaded, so a committed manifest never points at a missing blob.
3. **Pull** — teammates (or CI) download whatever the manifest lists that is missing locally. Downloads are verified against the manifest hash before touching your project.

Editing a tracked file is just: edit → **Push** → commit the manifest change. Switching branches: checkout → **Pull**.

## Requirements

- Unity **2021.3** or newer
- A git client (UPM installs the package via git)
- One of: an S3-compatible bucket (Cloudflare R2 recommended) or a Google account

## Install

**Package Manager UI**: `Window > Package Manager` → `+` → *Add package from git URL*:

```
https://github.com/Plumvery/UniLFS.git
```

**Or `Packages/manifest.json`**:

```json
{
  "dependencies": {
    "com.plumvery.unilfs": "https://github.com/Plumvery/UniLFS.git"
  }
}
```

Pin a version with a tag: `https://github.com/Plumvery/UniLFS.git#v0.1.0`

## Quick start (Cloudflare R2)

1. Create an R2 bucket and an API token with *Object Read & Write* — [step-by-step guide](Documentation~/setup-r2.md).
2. In Unity, open `Edit > Project Settings > UniLFS`:
   - Provider: **S3 compatible**
   - Endpoint: `https://<account-id>.r2.cloudflarestorage.com`
   - Bucket: your bucket name, Region: `auto`
   - Access Key ID / Secret Access Key (stored per-user, never committed)
3. Press **Test Connection**.
4. Select big assets in the Project window → right-click → `UniLFS > Track Selected`.
5. Open `Window > UniLFS` → **Push**.
6. Commit `unilfs.manifest.json`, `.gitignore`, `ProjectSettings/UniLFSSettings.json` and the assets' `.meta` files.
   If the files were already committed to git before, run the `git rm --cached` commands UniLFS prints to the Console.

Teammates then: clone → enter their credentials in Project Settings → `Window > UniLFS` → **Pull**. (A Console warning also reminds anyone opening a project with missing tracked files.)

Google Drive instead? See [Documentation~/setup-google-drive.md](Documentation~/setup-google-drive.md).

## The UniLFS window (`Window > UniLFS`)

| Button | What it does |
|--------|--------------|
| Refresh | Re-checks every tracked file (cheap — hashes are cached by mtime+size) |
| Push | Uploads new/changed blobs, then updates the manifest |
| Pull | Downloads files that are missing locally |
| Restore Modified | Overwrites locally modified files with the manifest version (asks first) |
| Track / Untrack Selected | Same as the `Assets > UniLFS` context menu |

File states: **up to date** (matches manifest) / **modified** (local edit not pushed) / **missing** (needs Pull).

## Configuration & credentials

| File | Committed? | Contents |
|------|-----------|----------|
| `unilfs.manifest.json` | ✅ | tracked paths + SHA-256 + size |
| `ProjectSettings/UniLFSSettings.json` | ✅ | provider, endpoint, bucket, folder ID, ... |
| `.gitignore` (managed block) | ✅ | tracked file paths, credential file |
| `UserSettings/UniLFS.json` | ❌ (auto-gitignored) | access keys, OAuth refresh token |

Environment variables override everything (useful for CI):
`UNILFS_S3_ACCESS_KEY_ID`, `UNILFS_S3_SECRET_ACCESS_KEY`, `UNILFS_DRIVE_CLIENT_ID`, `UNILFS_DRIVE_CLIENT_SECRET`, `UNILFS_DRIVE_REFRESH_TOKEN`.

Credentials are stored in plain text in `UserSettings/UniLFS.json` (like `~/.aws/credentials`). UniLFS force-includes that file in its `.gitignore` block, but treat the file like any other secret.

## CI

```sh
Unity -batchmode -nographics -quit -projectPath . \
  -executeMethod UniLFS.Editor.UniLfsCli.Pull
```

`Pull` / `Push` / `Status` are available; errors make the process exit non-zero. Full examples (GitHub Actions): [Documentation~/ci.md](Documentation~/ci.md).

## Merge behavior

The manifest is sorted with one line per file, so two people tracking *different* files merge cleanly. If two people change the *same* file you get a one-line conflict — pick the hash you want and run **Pull** (use **Restore Modified** to overwrite your local copy). Blobs for both versions exist remotely, so nothing is lost either way.

## Limitations (v0.1)

- No file locking (as with plain git — coordinate who edits shared binaries)
- No garbage collection of old blobs yet (storage is cheap; `unilfs prune` is on the roadmap)
- Single-request uploads: ~5 GB per-object limit on R2/S3
- Google Drive is best for solo/small-team use — see the rate-limit and quota notes in its guide
- Editor-only: files must be pulled before building (that is what the CI entry point is for)

## Roadmap

- Blob pruning / GC
- Track-by-pattern (e.g. auto-track everything under a folder)
- Multipart uploads
- OpenUPM listing

PRs and issues welcome.

## License

[MIT](LICENSE.md)
