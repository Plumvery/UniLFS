# Changelog

All notable changes to this package will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
