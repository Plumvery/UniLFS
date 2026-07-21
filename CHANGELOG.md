# Changelog

All notable changes to this package will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.3.0] - 2026-07-22

### Added

- New **not pushed** file state (fourth colour) in `Window > UniLFS`. The list previously compared files against the manifest only, which says nothing about whether a blob was ever uploaded — a freshly tracked file matched the manifest immediately and showed as "up to date" while existing nowhere but the local disk. Blobs are recorded as confirmed-in-storage whenever a Push uploads them (or finds them already there), a Pull downloads them, or Verify checks them, so the state costs no network calls. The record lives under `Library/UniLFS/`, is scoped per storage location (repointing at another bucket does not inherit confirmations), and is safe to delete.
- Setup prompt on editor start: when a project tracks files but the storage provider is not ready, UniLFS asks once per session and offers to sign in (Google Drive) or open the settings. Can be muted per project; never shown in batch mode.
- Setup banner in `Window > UniLFS` with a direct "Sign in with Google" button, so the sign-in step is not hidden in Project Settings.

### Fixed

- Push, Pull, Track, Untrack and Status can no longer run at the same time. The window, Auto Pull/Push and the asset menu each guarded only themselves, so a manual Push overlapping an Auto Push meant whichever saved the manifest last silently discarded the other's entries. They now share a process-wide lock; losers report that an operation is already running instead of racing, and the window queues its refresh rather than showing a stale list.
- Push/Pull progress no longer jumps around while transfers run in parallel. Progress is now aggregated across all in-flight transfers, weighted by bytes rather than file count, and can never run backwards; each operation fills the bar from 0 to 100% once instead of restarting for every hash/check/transfer stage. The label sticks to the longest-running file rather than cycling through every worker.
- The progress bar stays live during the status re-check that follows a Push or Pull, instead of sitting at 100%.
- `Project Settings > UniLFS`: page padding and label width now match built-in settings pages, long labels and wrapped help text are no longer clipped, and the Google Drive account and folder rows line up with the fields above them.
- Credentials are written to disk when a field loses focus instead of on every keystroke — typing a secret no longer rewrote `UserSettings/UniLFS.json` and the managed `.gitignore` block once per character.
- Connection test failures are shown as errors rather than as an unstyled message, and each provider lists what is still missing before Push/Pull can work.

## [0.2.0] - 2026-07-21

### Added

- Auto Push: when tracked files have local changes that were never uploaded, UniLFS detects it (on focus changes; in Automatic mode also right after asset saves/imports) and asks or uploads in the background (configurable: Ask / Automatic / Off).
- `UniLFS.Editor.UniLfsCli.Verify`: batch-mode check that fails when the manifest references blobs missing from remote storage.
- `Documentation~/ci/verify_manifest.py`: the same verify gate without Unity (Python stdlib only) for CI workflows and optional pre-push git hooks, for both S3-compatible and Google Drive providers.

## [0.1.0] - 2026-07-21

### Added

- Track / untrack large files from the Project window context menu (`Assets > UniLFS`).
- Manifest file (`unilfs.manifest.json`) with SHA-256 content hashes, one line per file for merge-friendly diffs.
- Automatic managed block in the project root `.gitignore` for tracked files and per-user credentials.
- S3-compatible storage provider (Cloudflare R2, Amazon S3, MinIO, ...) with a dependency-free AWS Signature V4 implementation.
- Google Drive storage provider with OAuth loopback sign-in (PKCE) and resumable uploads.
- `Window > UniLFS` management window: status, Push, Pull, Restore.
- Auto Pull without git hooks: on editor start / focus regain after the manifest changed (e.g. right after `git pull`), UniLFS asks to download missing files (configurable: Ask / Automatic / Off).
- `Project Settings > UniLFS` configuration UI with per-user credential storage and connection test.
- Content-addressed remote layout (`objects/<aa>/<sha256>`) with hash-verified downloads.
- Batch mode CLI entry points for CI: `UniLFS.Editor.UniLfsCli.Pull` / `Push` / `Status`.
- Editor tests for the manifest format, .gitignore management, path rules and the SigV4 signer.
