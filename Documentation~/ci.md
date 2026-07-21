# CI integration

Builds need the real files on disk, so run a UniLFS **Pull** after checkout and before the build step.

## Entry points

```
UniLFS.Editor.UniLfsCli.Pull     downloads everything missing; fails the process on any error
UniLFS.Editor.UniLfsCli.Push     uploads local changes (rarely needed in CI)
UniLFS.Editor.UniLfsCli.Status   logs the state of every tracked file
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
