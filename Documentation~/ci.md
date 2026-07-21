# CI integration

Builds need the real files on disk, so run a UniLFS **Pull** after checkout and before the build step.

## Entry points

```
UniLFS.Editor.UniLfsCli.Pull     downloads everything missing; fails the process on any error
UniLFS.Editor.UniLfsCli.Push     uploads local changes (rarely needed in CI - see below)
UniLFS.Editor.UniLfsCli.Verify   fails when the manifest references blobs missing from storage
UniLFS.Editor.UniLfsCli.Status   logs the state of every tracked file
```

> **Why CI cannot upload for you:** tracked files are gitignored, so a CI
> checkout only contains the manifest - the actual bytes exist solely on the
> machine that edited them. Uploading therefore has to happen client-side
> (this is true for git-lfs as well). UniLFS closes the gap from both sides:
> the **Auto Push** editor setting uploads changes as soon as they happen, and
> the **verify gate** below makes CI fail loudly if a manifest ever lands
> without its blobs.

## Verify gate without Unity

[`Documentation~/ci/verify_manifest.py`](ci/verify_manifest.py) checks that every
blob in `unilfs.manifest.json` exists in storage using only the Python 3
standard library - no Unity license, no pip packages, runs in seconds.

Copy the script into your project repo (e.g. `.github/scripts/`), or fetch it
pinned to a UniLFS release in the workflow itself:

```yaml
name: UniLFS verify
on: [push, pull_request]
jobs:
  verify:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - name: Verify UniLFS blobs exist in storage
        env:
          UNILFS_S3_ACCESS_KEY_ID: ${{ secrets.UNILFS_S3_ACCESS_KEY_ID }}
          UNILFS_S3_SECRET_ACCESS_KEY: ${{ secrets.UNILFS_S3_SECRET_ACCESS_KEY }}
        run: |
          curl -fsSLO https://raw.githubusercontent.com/Plumvery/UniLFS/v0.2.0/Documentation~/ci/verify_manifest.py
          python3 verify_manifest.py .
```

(For Google Drive set `UNILFS_DRIVE_CLIENT_ID` / `UNILFS_DRIVE_CLIENT_SECRET` /
`UNILFS_DRIVE_REFRESH_TOKEN` instead. A read-only storage token is enough.)

The same script works as an optional client-side **pre-push git hook**, blocking
a `git push` whose manifest points at blobs that were never uploaded:

```sh
#!/bin/sh
# .git/hooks/pre-push (chmod +x)
python3 .github/scripts/verify_manifest.py . || {
  echo "UniLFS: unpushed blobs - open Unity and press Push (or enable Auto Push)."
  exit 1
}
```

Plain command line:

```sh
# Windows
"C:\Program Files\Unity\Hub\Editor\<version>\Editor\Unity.exe" ^
  -batchmode -nographics -quit -projectPath . ^
  -executeMethod UniLFS.Editor.UniLfsCli.Pull -logFile -

# macOS / Linux
unity -batchmode -nographics -quit -projectPath . \
  -executeMethod UniLFS.Editor.UniLfsCli.Pull -logFile -
```

Environment variables used by the CLI:

| Variable | Meaning |
|----------|---------|
| `UNILFS_S3_ACCESS_KEY_ID` / `UNILFS_S3_SECRET_ACCESS_KEY` | S3-compatible credentials |
| `UNILFS_DRIVE_CLIENT_ID` / `UNILFS_DRIVE_CLIENT_SECRET` / `UNILFS_DRIVE_REFRESH_TOKEN` | Google Drive credentials (mint the refresh token once on a dev machine) |
| `UNILFS_PULL_RESTORE_MODIFIED=1` | make Pull overwrite locally modified files too |

## GitHub Actions (game-ci)

Two common shapes:

### a) Separate pull step

```yaml
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4

      - uses: actions/cache@v4
        with:
          path: Library
          key: Library-${{ runner.os }}

      # 1. pull UniLFS assets
      - uses: game-ci/unity-builder@v4
        env:
          UNITY_LICENSE: ${{ secrets.UNITY_LICENSE }}
          UNITY_EMAIL: ${{ secrets.UNITY_EMAIL }}
          UNITY_PASSWORD: ${{ secrets.UNITY_PASSWORD }}
          UNILFS_S3_ACCESS_KEY_ID: ${{ secrets.UNILFS_S3_ACCESS_KEY_ID }}
          UNILFS_S3_SECRET_ACCESS_KEY: ${{ secrets.UNILFS_S3_SECRET_ACCESS_KEY }}
        with:
          targetPlatform: StandaloneWindows64
          buildMethod: UniLFS.Editor.UniLfsCli.Pull

      # 2. actual build
      - uses: game-ci/unity-builder@v4
        env:
          UNITY_LICENSE: ${{ secrets.UNITY_LICENSE }}
          UNITY_EMAIL: ${{ secrets.UNITY_EMAIL }}
          UNITY_PASSWORD: ${{ secrets.UNITY_PASSWORD }}
        with:
          targetPlatform: StandaloneWindows64
```

### b) One custom build method that pulls first

```csharp
// Assets/Editor/CiBuild.cs in your project
public static class CiBuild
{
    public static void Build()
    {
        UniLFS.Editor.UniLfsCli.Pull();   // throws (fails CI) on any download error
        BuildPipeline.BuildPlayer(
            EditorBuildSettings.scenes,
            "Builds/Game.exe",
            BuildTarget.StandaloneWindows64,
            BuildOptions.None);
    }
}
```

```yaml
      - uses: game-ci/unity-builder@v4
        env:
          UNITY_LICENSE: ${{ secrets.UNITY_LICENSE }}
          UNILFS_S3_ACCESS_KEY_ID: ${{ secrets.UNILFS_S3_ACCESS_KEY_ID }}
          UNILFS_S3_SECRET_ACCESS_KEY: ${{ secrets.UNILFS_S3_SECRET_ACCESS_KEY }}
        with:
          targetPlatform: StandaloneWindows64
          buildMethod: CiBuild.Build
```

## Tips

- Cache `Library/` — it also contains the UniLFS hash cache (`Library/UniLFS/statecache.json`), making Pull's status check instant.
- Downloaded files land where the manifest says; `AssetDatabase.Refresh()` runs automatically after the CLI Pull, before your build method continues.
- Read-only CI storage credentials are enough for Pull (e.g. an R2 token scoped to *Object Read*).
