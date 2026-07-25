# Design: staged tracking (Track stops writing the manifest)

Status: implemented in 0.4.0. This is why the split exists and what it
guarantees; the code is in `UniLfsStagedPaths`, `UniLfsGitExclude` and the
Track/Push/Status paths of `UniLfsCore`.

## The problem

`unilfs.manifest.json` is committed to git, and **Track writes to it
immediately** — before anything has been uploaded. That single fact is behind
two separate failures.

### 1. A committed manifest can name a blob nobody can download

Track records the hash; the bytes only leave the machine on Push. Commit in
between and every teammate gets a manifest entry pointing at an object that is
not in the bucket. The whole
[CI verify gate](ci.md#verify-gate-without-unity) exists to catch this after
the fact, and the failure it catches is one the format allows by construction.

### 2. Track claims a sync that never happened, and Pull acts on it

Track ends with `cache.RecordSynced(rel, hash)` — the per-machine baseline that
answers "which manifest hash was this file last in sync with". For freshly
tracked content that agreement is with itself: nothing was uploaded, nobody
else has seen it.

When a real manifest then arrives naming somebody else's version of the same
path, the three-way rule reads `local == baseline` and answers **Outdated** —
"only the manifest moved, your copy is the one you last synced" — and Pull
overwrites the local file. The content it overwrites was never pushed, so it
existed on exactly one disk and now exists on none.

This is pinned today by
`PullPushStatePatternTests.DifferentContentTrackedIndependentlyFollowsTheManifestLineThatWon`,
which asserts the current behaviour rather than the wanted one.

### Why the one-line fix does not hold

Dropping the `RecordSynced` call from Track is not enough.
[`StatusUnlockedAsync`](../Editor/Core/UniLfsCore.cs) re-adopts a baseline
whenever local content and the manifest agree:

```csharp
if (entry.State == UniLfsFileState.UpToDate)
{
    cache.RecordSynced(f.path, f.hash);   // "agreement is itself a synced state"
    entry.BaselineKnown = true;
}
```

Right after Track those two agree by construction, so the first status check —
one runs on a timer — puts the false baseline straight back. Keeping the fix
would mean gating that adoption on "the blob is confirmed in storage", i.e. on
a per-machine cache a fresh clone does not have and that gets pruned.

That is a patch on a symptom. The real problem is that `unilfs.manifest.json`
holds two different kinds of entry — *content the team has* and *content only I
have* — and the format cannot tell them apart.

## The change

Split the two kinds into two files.

| file | committed? | written by | meaning of an entry |
| --- | --- | --- | --- |
| `unilfs.manifest.json` | yes | **Push only** | this content was in storage when the entry was written |
| `unilfs.staged.json` | no | Track, Untrack, Push | this machine wants this path tracked; nothing has been uploaded yet |

### Where it lives

The project root, next to the manifest:

```
your-project/
├── unilfs.manifest.json     ← committed: what the team has
├── unilfs.staged.json       ← not committed: what this machine has asked for
├── Library/UniLFS/          ← per-machine caches, safe to delete
└── .gitignore               ← managed block, generated from the manifest
```

Hidden from git by one constant line in the managed `.gitignore` block, the way
`UserSettings/UniLFS.json` already is. Constant, so the committed block stays a
pure function of the committed manifest.

**Not `Library/UniLFS/`**, even though staging is per-machine like everything
else in there. `Library/` is documented — by this package, in
[`UniLfsStateCache`](../Editor/Core/UniLfsStateCache.cs) — as safe to delete,
and it is the folder people are told to delete when Unity misbehaves.
Everything else under it is derived and comes back on its own; staging is the
only per-machine state that cannot be recomputed from anything. Losing it does
not stay quiet either: the `info/exclude` block is regenerated from staging on
the next Track or Push, so an empty staging file empties that block, those
large files stop being ignored by anything, and the next `git add -A` puts them
in the history. "Safe to delete" has to keep meaning what it says, so staging
goes where the manifest is.

`UserSettings/` is the other defensible answer — Unity's own convention for
per-user, uncommitted state, and UniLFS already keeps credentials there. It
loses on discoverability: staging is a thing a person has to be able to find
and read when Push says something they did not expect, and a second file beside
the manifest says that better than a folder they never open.

Staging is the working copy of the tracking decision; the manifest is the part
of it that has been made real. Roughly git's index and its commits, for the
same reason: the intent to include something and the record that it exists are
not the same statement, and writing them to one file makes each of them a lie
in some state.

### Staging holds intent, never derived facts

```json
{
  "version": 1,
  "paths": ["Assets/Art/dragon.fbx"],
  "resolveLocal": ["Assets/Art/atlas.psd"]
}
```

No hash, no size, no GUID. Push re-hashes at push time and re-reads the `.meta`
then, so anything else in here would be a second copy of the truth with its own
way of going stale — which is the bug being fixed, one file over.

`resolveLocal` is the "keep mine" half of resolving a conflict: an explicit,
recorded decision that this machine's content wins for that path, consumed by
the next Push. Today that decision is made by re-running Track, which is why
Track can silently overwrite a manifest line.

### Invariants

1. An entry in `unilfs.manifest.json` names a blob that was confirmed present
   in storage when the entry was written. Only Push writes entries.
2. `unilfs.staged.json` contains no derived data. Deleting it loses intent, not
   facts; re-running Track restores it.
3. The managed `.gitignore` block is derived from **the manifest alone**, so a
   committed file never depends on a local one. Staged paths are ignored
   locally instead — see [Two ignore files](#two-ignore-files).
4. A baseline is recorded only when local content, the manifest entry and
   storage all agree — by Push and Pull. Track never records one.
5. Untrack removes the path from both files. Removing it from the manifest is a
   committed change; removing it from staging is not.

Invariant 4 needs no change to the three-way rules and no change to the
baseline adoption quoted above: after the split there is no manifest entry to
agree with until something has actually been pushed, so the case that made
adoption unsound cannot arise. An entry that is in the manifest and matches
local content really is the team's line — adopting a baseline for it stays
correct.

## Two ignore files

A tracked file has to be hidden from git from the moment it is tracked, but the
two audiences for that are not the same. The manifest's paths have to be
ignored **for everyone who clones the repository**; a staged path only has to
be ignored **here**, because nobody else's checkout can even contain it.

Deriving one committed file (`.gitignore`) from a local, gitignored one
(`unilfs.staged.json`) would make the committed file machine-dependent: staging
three files adds three lines, committing that shares them, and the next
person's Track or Push regenerates the block from *their* staging and drops
them again. The managed block would ping-pong between machines and produce
merge conflicts in the one file that should be boring.

| file | derived from | committed | protects |
| --- | --- | --- | --- |
| `.gitignore` managed block | the manifest | yes | everyone with a clone |
| `.git/info/exclude` managed block | staging | no — git never tracks it | this machine, until Push |

`.git/info/exclude` is per-checkout and invisible to git, which is exactly the
shape of a staged path. It also makes the committed block a pure function of
the committed manifest, so CI can regenerate it and fail on drift.

Implementation notes:

- The git directory has to be found, and `.git` is not always a directory: in a
  worktree or a submodule it is a *file* containing `gitdir: <path>`. Both
  forms need handling.
- Paths in `info/exclude` are relative to the repository root, not to the
  project root the way `.gitignore` is, so a Unity project in a subdirectory
  needs that prefix. `UniLfsGitIgnore.EscapeGitIgnorePath` anchors with a
  leading `/` and would need the prefix threaded through.
- Best-effort: no git checkout, no exclude file. Status reports staged paths it
  could not ignore rather than failing anything.
- **Write order on promotion: add to `.gitignore`, then remove from
  `info/exclude`.** Both ignoring it for an instant is harmless; neither
  ignoring it for an instant is the window this exists to close.

## Operations

| operation | reads | writes | notes |
| --- | --- | --- | --- |
| **Track** | staged, manifest | staged, `info/exclude` | never the manifest, never `.gitignore`, never a baseline. A path already in the manifest is a no-op (see below) |
| **Push** | staged, manifest | manifest (confirmed only), staged, baseline, `.gitignore`, `info/exclude` | promotes staged paths into the manifest after upload, then drops them from staging |
| **Pull** | manifest | asset files, `.meta`, baseline | staged-only paths have no entry, so there is nothing to download — unchanged code |
| **Untrack** | both | both, `.gitignore`, `info/exclude` | |
| **Status** | both | baseline (adoption only) | synthesizes an entry for staged-only paths; reports any it could not ignore |
| **Verify** | manifest | — | now expected to always pass; still catches a bad merge or a hand-edited manifest |
| **MetaGuard** | manifest | `.meta`, placeholders | skips staged-only paths: no blob to stand in for, no recorded GUID to restore |

### Track on an already-tracked file becomes a no-op

Editing a tracked file has never needed Track — Push picks up `Modified` files
on its own. The only thing re-tracking does today is resolve a conflict in
favour of the local copy, and it does it by rewriting a committed file with no
upload behind it. After the split that decision is `resolveLocal` in staging,
surfaced as its own button, and Track goes back to meaning one thing: start
tracking this path.

### Push promotes

```
for each staged path:
    hash it
    upload the blob unless storage already has it
    once storage confirms the blob:
        write the manifest entry (path, hash, size, guid read now)
        record the baseline
        drop the path from staging
```

Failure leaves the path staged, which is the right resting state: still
tracked here, still not visible to anyone else. The existing rule — the
manifest is only written for blobs storage confirmed — is unchanged, it just
now covers new files too.

### The states two clones can be in

Adding a path to staging that the incoming manifest also names is the case the
whole change is for:

| local content vs manifest entry | baseline | staged? | state | who acts |
| --- | --- | --- | --- | --- |
| no manifest entry | — | yes | **Staged** (new) | Push |
| equal | any | any | UpToDate | — (drop from staging) |
| differs | none | **yes** | **Conflicted** | the human |
| differs | none | no | Modified | Push (pre-0.3.3 cache, wiped `Library/`) |
| differs | == local | no | Outdated | Pull |
| differs | == manifest | no | Modified | Push |
| differs | neither | no | Conflicted | the human |

The third row is the fix. Today it reads Outdated (via a baseline Track had no
business recording) and Pull overwrites; being staged is the evidence that this
machine tracked the path independently, which makes "neither version wins
automatically" the honest answer.

`UniLfsThreeWay.Classify` gains the staged flag as a fourth input, or the caller
overrides its answer — either way the rule stays readable as a merge-base
decision.

## Display

- New label **staged** for the new state, next to the existing blue *not
  pushed*. They look the same on purpose: to the person looking, both mean
  "nobody else can see this yet". The difference is that *staged* is definite
  (there is no manifest entry) while *not pushed* is inferred from a
  per-machine cache that a fresh clone starts empty.
- The window's summary line gains a staged count.
- **Track Selected** stops resolving conflicts. A **Keep Mine** button next to
  **Restore Modified** writes `resolveLocal` instead — the two halves of the
  same decision, in the same place, which is not true today.
- CLI `Status` and the Push summary report staged separately from up to date.

## Migration from 0.3.3 and earlier

- Existing manifests may already contain entries that were never pushed. They
  are treated as the team's line, exactly as now; Refresh and the CI gate keep
  reporting the ones storage cannot back. No rewrite, no format bump —
  `unilfs.manifest.json` is unchanged.
- `unilfs.staged.json` starts absent, which reads as empty. The first Track
  creates it and writes its `info/exclude` line.
- The managed `.gitignore` block keeps being generated from the manifest, which
  is what 0.3.3 already does, so existing projects need no user action and see
  no diff.
- **Downgrade is safe**: an older UniLFS does not read `unilfs.staged.json` and
  generates the same `.gitignore` block from the same manifest. It leaves the
  `info/exclude` block behind untouched, which keeps working — an ignore rule
  needs nobody to maintain it. Only staged-but-unpushed paths are forgotten,
  and re-running Track restores them.

## What this does not solve

- Two people pushing the same path still meet in a one-line manifest conflict
  that git makes a person resolve. Unchanged, and correct.
- No file locking. Unchanged.
- **The window shrinks but does not close.** A staged path is ignored through
  `.git/info/exclude`, which needs a git checkout to write to. Outside one — a
  project not yet under version control, an exotic layout the git-dir lookup
  does not understand — a staged file is not ignored by anything until the
  first Push promotes it, and a `git add -A` in that window puts it in the
  history. Status has to say so for every staged path it could not ignore;
  that report is the only defence left in that case.
- Deleting `unilfs.staged.json` loses the intent to track those paths, and the
  next Track or Push regenerates `info/exclude` from the now-empty staging —
  so they stop being ignored and are one `git add -A` from the history. Nothing
  *committed* changes, which is what deriving `.gitignore` from the manifest
  alone buys; the local half is still lost. Hence the placement above, and
  hence Status reporting staged paths nothing is ignoring.

## Work order

Each step is separately shippable and testable.

1. `UniLfsStagedPaths` — load, save (atomic, same as the manifest), add,
   remove, `resolveLocal`. Unit tests, including a corrupt file reading as
   empty.
2. `UniLfsGitExclude` — find the git directory (`.git` as a directory *and* as
   a `gitdir:` file), write a marker-delimited block into `info/exclude` with
   paths anchored to the repository root, no-op without a checkout. Reuses the
   block-rewriting logic `UniLfsGitIgnore` already has, so factor that out
   rather than copying it. Unit tests for both `.git` shapes, a project in a
   subdirectory, and no repository at all.
3. Track writes staging and `info/exclude` only; stops writing the manifest,
   `.gitignore` and the baseline.
4. Push reads staging, promotes on confirmation, drops promoted paths — adding
   the `.gitignore` line *before* removing the `info/exclude` one.
5. Status synthesizes staged-only entries, applies the Conflicted rule above,
   and reports staged paths that nothing is ignoring.
6. Untrack clears both.
7. MetaGuard skips staged-only paths.
8. Window and CLI: staged count, **Keep Mine**, Track Selected no longer
   resolves.
9. README (*How it works* steps 1-2, the on-disk tree, *Merge behavior*),
   README_JA, CHANGELOG 0.4.0.
10. Tests: a staging group in `PullPushStatePatternTests`, and
    `DifferentContentTrackedIndependentlyFollowsTheManifestLineThatWon` flips
    from *Outdated, overwritten* to *Conflicted, kept* — it is the regression
    test for this whole document.
