# Changelog

All notable changes to this package will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.5.3] - 2026-09-04

### Fixed

- **A fresh clone keeps the import settings of tracked assets.** `.meta` files are committed, but a tracked asset is not — so a clone has `Foo.psd.meta` with no `Foo.psd`, and Unity discards an orphaned `.meta` before any managed code runs. `UniLfsMetaGuard` rebuilt it from the GUID recorded in the manifest, which saved every reference to the asset but carried no importer section: non-default import settings were silently reset to their defaults, and the `.meta` stayed dirty in the working tree until somebody ran `git checkout -- <path>.meta` by hand. The guard now runs that itself, before rebuilding anything — the committed `.meta` comes back exactly as it was, importer section and all, and the working tree is clean again. Rebuilding from the manifest is still there for what git cannot answer: no git on `PATH`, or a `.meta` that was never committed. That case keeps the warning it always had; the restore only logs, because nothing was lost.

### Changed

- UniLFS now runs the `git` command line, in this one place and nowhere else. Everything else it needs from a checkout is a file it reads or writes directly, but a committed blob is not — reaching it means the index format, loose objects and packfiles, to re-implement one command every machine with a git checkout already has. A missing git, a timeout and a failure are all treated the same way: restore nothing and fall back to the previous behaviour. Paths git does not know are filtered out first with `ls-files`, because one unknown pathspec makes a whole `git checkout` call fail and would cost every other file its restore.

## [0.5.2] - 2026-09-04

### Fixed

- **Auto Pull and Auto Push no longer take the editor hostage while they wait for an answer.** In **Ask** mode — the default — both opened `EditorUtility.DisplayDialog` from editor startup, and a modal dialog stops the main thread until somebody clicks a button. The only guard was `Application.isBatchMode`, which a GUI editor driven by an automation harness (a Unity MCP server, a CLI-driven editor loop, a remote runner) does not trip: the first launch after a teammate pushed hung indefinitely, and the log said nothing at all about why. The same questions are now asked from a floating window that never blocks the main thread; closing it counts as **Later**, so the Console records the outcome either way and "nobody answered" is no longer indistinguishable from "answered Later". The setup reminder (**Sign in with Google** / **Open Settings** / **Don't ask again**) moved with them — it opened the same kind of dialog on the first launch of a fresh clone.
- **A check that never ran no longer counts as one that did.** Auto Pull marked the manifest version handled *before* running its status check, so two exits that decided nothing hid it for the rest of the editor session: a status check that threw, and a prompt that arrived while another operation held the lock — the latter returning without a single Console line, which in the log is indistinguishable from a dialog nobody answered. Only the "busy" path handed the stamp back. Both Auto Pull and Auto Push now record what they asked about when the answer arrives, so anything ending without one — a failed check, a busy editor, a domain reload that took the window with it — is retried on the next focus change. The "could not check tracked files" warning is written once per manifest version rather than once per retry.

### Added

- **`UNILFS_NO_PROMPTS=1`** stops UniLFS opening prompts on this machine at all: **Ask** writes the Console line **Off** writes, and nothing waits for an answer. For editors that are driven rather than watched — Auto Pull and Auto Push live in committed project settings, so turning them off has always been a decision for the whole team and never one the single unattended machine could make for itself.

## [0.5.1] - 2026-08-21

### Fixed

- **Google sign-in now asks which account to use.** The consent URL requested `prompt=consent`, and Google skips the account chooser when the browser holds a single session — so pressing **Sign in with Google** signed you in as whichever account that browser happened to have open, without asking and without saying whose it was. On the wrong account every Pull then failed with `Blob xxxxxxxx... was not found in the Google Drive folder`, a message that reads as "nobody pushed it yet" and sent people to interrogate the pusher rather than the account they were signed in with. The URL now asks for `select_account` as well, so the chooser appears every time and the choice is made deliberately.

### Added

- **The signed-in address is recorded, shown and logged.** Sign-in now reads the account's email from the Drive API and puts it where the question comes up: *Signed in as ...* under **Account** in `Edit > Project Settings > UniLFS`, the Console line the startup prompt writes, and the failure messages that hinge on it. It is stored next to the refresh token in the per-user `UserSettings/UniLFS.json` as `driveAccountEmail` and cleared with it on **Sign out** — informational only, since the token remains the credential. Settings files written by earlier versions load unchanged and read *Signed in* until the next sign-in fills the address in, as does a sign-in whose lookup failed; nothing depends on the answer. When `UNILFS_DRIVE_REFRESH_TOKEN` is set the row says so instead (*Signed in via UNILFS_DRIVE_REFRESH_TOKEN*), because Push and Pull use that token rather than the stored one and naming the stored account would be wrong.
- **Test Connection also checks that the account can see inside the folder.** Reading a folder's name and listing its contents are separate permissions, so the old check — which only fetched the folder's metadata — reported "Connected to Google Drive folder 'X'." for an account that could not see a single blob in it, minutes before every Pull failed. It now lists the folder as well, and says when the listing comes back empty: harmless for a brand-new folder, and the explanation you need when teammates have already pushed.

### Changed

- The "blob not found" message names the account it searched as, and offers the cause it used to omit: the folder's contents may not be visible to that account. Sharing the folder with the address in the message, or signing in with a different one, is the fix in that case.

## [0.5.0] - 2026-08-11

### Added

- **`unilfs.track`: the project states once which files belong in storage.** Until now that answer only existed in whoever remembered to right-click a file, so a new `.psd` reached git whenever nobody did — and a new team member had no way to find out what the rule even was. `unilfs.track` is a committed plain-text file of gitignore-style patterns (`*.psd`, `Assets/Movies/`, `!Assets/UI/*.psd`, `# comments`), edited in a text editor like the `.gitattributes` git-lfs uses, except nothing generates it. Committing it is what turns the rule into the project's rather than one machine's.
- **Track Matching** (`Window > UniLFS`) sweeps the whole project and tracks every match nothing tracks yet — what to press after writing the file, or after a `git pull` brings a teammate's new pattern. Files already in the manifest or in staging are counted and skipped rather than re-examined, so a sweep over a settled project reads no file content at all.
- **Auto Track** (`Edit > Project Settings > UniLFS`, on by default) tracks matching files as they are imported or moved, which is the point of writing the patterns down: the file is out of git from the moment it lands. It does nothing at all until the project has a `unilfs.track`, so existing projects are unaffected until they opt in.
- Both routes end in the same Track as the menu item, so nothing about tracking changed: files are staged on this machine and hidden from git, and **Push** remains the only thing that uploads content and writes a manifest entry. Patterns decide which paths are handed to Track and nothing else.
- Matching ignores case, and the last matching line wins so a later line can re-include what an earlier one excluded. Whatever a line says, `.meta` files, UniLFS's own files and everything under `Library/`, `Temp/`, `Logs/`, `obj/`, `UserSettings/` and `.git/` never match — the pattern file cannot express a path Track itself would refuse. Lines it cannot read (`..` segments, say) are reported in Project Settings and by the sweep rather than silently matching nothing.
- `UniLfsCli.Track` for batch mode, so CI and scripts can track without a hand-written `-executeMethod` shim: `Unity -batchmode -nographics -quit -executeMethod UniLFS.Editor.UniLfsCli.Track`, then `...UniLfsCli.Push`.

## [0.4.1] - 2026-08-04

### Changed

- **Push no longer asks storage about blobs this machine already confirmed.** Every Push used to make one existence request per tracked blob — changed or not — so pushing a project where nothing moved cost a network round trip per file (and on Google Drive, a token refresh in front of them). Push now trusts the per-machine confirmation record (`Library/remote-*.json`), which is written on every successful upload, download and check, and asks only about blobs it has no proof for. A no-change Push and Pull now make no storage requests at all.
- The trade-off is deliberate and narrow: a blob deleted from the bucket *after* this machine confirmed it hides behind the stale record, where the old Push would have noticed by accident. That was always **Verify**'s job — Refresh in the window, `UniLfsCli.Verify` in CI — which asks storage for real, retracts confirmations it denies, and thereby makes the next Push re-upload. Deleting `Library/remote-*.json` also drops every confirmation, after which Push checks everything once and re-earns them.

## [0.4.0] - 2026-07-25

### Changed

- **Track no longer writes `unilfs.manifest.json`.** It stages the paths in a new local file and hides them from git; **Push** is what creates the manifest entry, after storage confirms the content. The manifest is a committed file, and an entry in it is a promise that the bytes are downloadable — a promise Track was in no position to make. The old order is where two separate failures came from, and both are gone: committing a manifest that references blobs nobody uploaded, and the one below.
- Track is now instant. It no longer hashes a file it is not about to upload, so tracking a folder of multi-gigabyte assets no longer reads all of them first.
- The GUID recorded next to an asset is read at Push time rather than at Track time, so a re-import that mints a new GUID reaches the rest of the team without anyone re-tracking anything.
- Re-running **Track Selected** on an already-tracked file no longer resolves a conflict in favour of the local copy — that was a committed manifest being rewritten by a command that reads as "start tracking this". Use **Keep Mine** (below).

### Added

- `unilfs.staged.json` (never committed, next to the manifest): the paths this machine has asked to track and has not pushed. It holds no hashes, sizes or GUIDs — only intent, so nothing in it can go stale. Deleting it loses the intent, not any fact; re-run Track.
- **staged** file state, for a file tracked here and in no manifest yet: nobody else can see it until you Push.
- **Keep Mine** button next to **Restore Modified**, the recorded half of resolving a conflict ("keep mine" vs "take theirs"). Because it is recorded rather than passed to one Push call, Auto Push honours it too.
- Staged files are hidden from git through `.git/info/exclude`, which git never tracks — including from a linked worktree or a submodule, where `.git` is a file and the exclude file lives elsewhere. The committed `.gitignore` block stays derived from the manifest alone, so it cannot differ between two machines. Outside a git checkout there is nowhere to write that, and UniLFS says so instead of pretending the file is hidden.

### Fixed

- Two people tracking the same path independently could lose one of the two versions without a word. Track recorded a sync baseline for content that had never been uploaded, so when the other person's manifest arrived the local copy read as **outdated** — "only the manifest moved" — and Pull replaced a file that existed on exactly one disk and in no bucket. It now reads as **conflicted** and neither Push nor Pull touches it until someone chooses.

## [0.3.3] - 2026-07-22

### Fixed

- A teammate updating an already-tracked file reached nobody. Pull downloaded only files *missing* from disk, and an updated file is not missing — it is sitting right there with the old bytes — so Pull skipped it, and Auto Pull never fired at all, because its check was literally "does this path exist?". The project kept the stale version with nothing on screen saying so. Only **Restore Modified** brought it down, and nothing pointed you at it.
- Push would then roll the change back. A stale copy and a local edit both read as `modified`, so Auto Push offered to upload it, and Push rewrote the manifest entry to the older local hash — undoing whoever pushed last, in a change git shows as an ordinary one-line manifest diff. `Assets > UniLFS > Track Selected` on an already-tracked file did the same thing.
- Files could sit at **not pushed** (blue) while their blob had never left storage. Every status check dropped confirmations for blobs the current manifest did not name, so any manifest change discarded proof that was still true, and the file stayed blue until someone pressed Refresh and paid for the round trip again. Confirmations outside the manifest are now kept (most recent 4096) instead of dropped on sight.
- Auto Pull and Auto Push each ran a status check on startup without waiting for the other, so whichever lost the race for the operation lock gave up silently.

### Added

- **outdated** and **conflicted** file states, splitting what `modified` used to cover. Local hash versus manifest hash cannot tell "I edited this" from "someone else pushed a newer one" — both are just `local != manifest`, and they need opposite buttons. UniLFS now records, per machine under `Library/UniLFS/`, which manifest hash each file was last in sync with, and compares against that the way a merge base decides which side of a diff moved.

  Pull downloads **outdated** files; Push refuses to touch them and says to Pull instead. **conflicted** (both sides moved) is left alone by Push and Pull, and resolved by hand: take the manifest's version with **Restore Modified**, or keep yours with **Track Selected** followed by Push — Track says in the Console when it resolved one that way, since it drops whatever the manifest named.

  Baselines are recorded by Track, Push and Pull, and adopted automatically whenever local and manifest already agree, so an existing project establishes them on its first status check with no re-tracking. A file that is *already* diverged at that moment has nothing to adopt: it reads `modified`, the conservative answer, but Auto Push now leaves it alone rather than guessing that the local side is the newer one. An explicit Push still takes it. The same applies after deleting `Library/UniLFS/`.

## [0.3.2] - 2026-07-22

### Fixed

- Tracked assets no longer come back under a new GUID when the project is opened without their content on disk — which silently broke every scene, prefab and Addressables reference pointing at them. Tracked files are gitignored, so a clone gets `Foo.mp4.meta` but not `Foo.mp4`; Unity discards a `.meta` it cannot match to an asset, and mints a fresh GUID once Pull finally brings the file back. Auto Pull could never prevent it: it runs from `EditorApplication.delayCall`, long after. The manifest now records each tracked file's GUID, and UniLFS puts a discarded `.meta` back from that record before the asset lands, so the import reuses the identity the rest of the project already references.
- `README.md` promised "GUIDs and references never break". That was the intent, not the behaviour.

### Added

- The manifest records each tracked file's Unity GUID next to its hash, and Pull recreates a missing `.meta` from it before writing the asset. Entries written by earlier versions carry no GUID — re-run Track on them to record one, or a clone still has nothing to restore from.

  A rebuilt `.meta` carries the GUID but not the original import settings; Unity fills those back in with defaults. Restoring the `.meta` from git is the better outcome, and UniLFS now warns and says so whenever it had to rebuild one. GUID first because the trade is not symmetric: a wrong GUID breaks references, while default import settings merely look wrong and can be set again.
- A placeholder is written at every tracked path whose content is missing. It does *not* win the race against Unity's startup scan — `[InitializeOnLoadMethod]` is the earliest hook managed code gets, and measured on 2022.3 the scan has already discarded orphaned `.meta` files by the time it runs, from a warm `Library` and a deleted one alike. What the placeholder does is keep the window shut afterwards: with an asset at the path, the restored `.meta` survives later refreshes instead of being discarded and rebuilt on every one, and Pull overwrites the placeholder in place under the same GUID. Expect import errors for those paths until Pull runs — the files really are not there yet.
- A `.meta` whose GUID disagrees with the manifest is reported as an error on editor start. It is the signature of this damage having already happened, and is otherwise invisible until something fails to load at runtime.

### Changed

- Manifest entries that do not name a path inside the project are refused with an error instead of being acted on. The manifest is committed, hand-editable and merge-resolved, and the startup guard writes files without anyone asking — so a `../..` entry from a bad merge would otherwise have created files outside the project on editor start.
- Placeholders read as **missing**, never as a local modification, everywhere UniLFS looks at a tracked file. Push skips them outright: it rewrites the manifest from whatever it just hashed, so uploading a stand-in would have pointed every clone at it and orphaned the real blob. Untrack clears any placeholder left at the path, which stops being gitignored the moment the entry is removed.

## [0.3.1] - 2026-07-22

### Changed

- **Refresh** in `Window > UniLFS` now also verifies the manifest against remote storage, instead of trusting only what this machine happened to record. The **not pushed** state added in 0.3.0 was built from local confirmations, so it answered "did *I* ever upload this?" rather than "is it in storage?": a fresh clone had confirmed nothing and showed every file as not pushed, and deleting `Library/` had the same effect. One existence request per distinct blob now settles it, and the check runs under the same lock as the rest, so it cannot overlap a Push.
- The verification is deliberately limited to the Refresh button. Opening the window, and the status re-check that follows a Push or Pull, stay local-only and cost no requests — Push and Pull already know what they moved.
- `UniLfsCore.StatusAsync` gained a `bool verifyRemote` overload returning a `UniLfsStatusReport` (file list plus what storage answered). The existing signature is unchanged, so `UniLfsCli.Status` and Auto Pull/Push keep their local-only behaviour.

### Fixed

- A blob deleted from storage no longer stays "up to date" forever. Confirmations were only ever added, so once a machine had seen a blob it kept believing it — a bucket emptied by hand, or a file that only ever reached a different bucket, still showed green. A check that gets a definitive "not there" now retracts the confirmation, while a check that *fails* (network, credentials) leaves it alone, since no answer is not the same as "absent".
- `UniLfsCore.VerifyRemoteAsync` takes the operation lock like every other operation. It rewrites the confirmation record, so a Verify overlapping a Push could previously undo part of what the Push had just recorded.

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
