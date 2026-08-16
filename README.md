# VerityWorkbench

VerityWorkbench is a local-first Windows research workbench for investigating whether person-specific facial, vocal, timing, and linguistic changes correlate with independently supported intentional deception under controlled conditions.

> Early development — Milestone 3 local media ingest. The application creates and edits persistent profiles, copies explicitly selected local MP4s into user-selected app-managed workspace assets, records their integrity metadata, and tracks cancellable ingest jobs. It does not yet validate MP4 contents with FFmpeg, extract features, train a model, or produce any behavioral score or probability.

The authoritative scientific, product, privacy, and architecture specification is [design_doc.md](design_doc.md).

## Scientific and use limitations

VerityWorkbench is not a general-purpose lie detector. Behavioral signals cannot establish whether a statement is factually true. A profile trained only on sincere-truth material could eventually support behavioral-deviation analysis, but it cannot produce a truth probability. An experimental intentional-deception classifier requires both independently supported sincere-truth and intentional-deception sessions, grouped evaluation, and prospective validation.

The default valid result is **Cannot determine**. A percent sign must not be displayed for an uncalibrated score. Even after separate prospective calibration, an output could only be described as an estimated probability of intentional deception under the validated subject/protocol/context; its complement is not factual truth.

Do not use this software for employment, policing, courts, immigration, healthcare, credit, housing, education, public benefits, or coercive interpersonal disputes. See the design document for the full prohibited-use policy.

## Current implementation

This repository is being implemented in small, testable slices.

- [x] .NET 10 solution with a WinUI 3 desktop shell
- [x] Main view with **Add Profile**, **Edit Profile**, and **Query Profile** choices
- [x] Working Add Profile draft form
- [x] Pseudonymous profile name and workspace/download folder pickers
- [x] Separate verified sincere-truth and verified intentional-deception MP4 lists
- [x] Explicit local MP4 selection, removal, duplicate prevention, and recording-label sorting
- [x] Workspace boundary validation and named-folder creation
- [x] Persistent Draft profiles loaded automatically after app restarts
- [x] Transactional SQLite storage with stable profile/video IDs and timestamps
- [x] Limited Edit Profile workflow for names, labels, ordering, additions, and archive state
- [x] Unit tests for domain, workspace, and persistence rules
- [x] Local MP4 staging, SHA-256 hashing, app-managed workspace promotion, and content deduplication
- [x] Persistent ingest jobs with live progress, cancellation, stale-job recovery, and cross-condition content conflict detection
- [ ] FFmpeg/ffprobe MP4 validation, stream-quality inspection, proxies, and audio extraction
- [ ] Direct-URL resumable downloads
- [ ] Processing-folder inspection and verified cleanup controls
- [ ] Playback, transcription, and feature extraction
- [ ] Training, grouped validation, calibration, and local inference
- [ ] Query Profile workflow
- [ ] One-file encrypted `.vwpkg` import/export

**Query Profile** remains deliberately unavailable. Edit Profile updates profile metadata and media eligibility but does not relocate workspaces, delete ingested assets, run FFmpeg, import packages, or export models. No placeholder or random scoring code exists.

## What to install

### To test this slice on the current development computer

Nothing else is required for the PowerShell workflow below. This computer has .NET SDK `10.0.400`, and the application has been restored, built, tested, and launch-checked successfully.

Python, FFmpeg, GPU drivers/toolkits, Whisper, and model files are **not needed** for this milestone. Do not install them yet. FFmpeg will first be required for the next media-probe and MP4-validation slice; exact pinned-build instructions will be added before that work begins.

### On another Windows development computer

Install:

1. Windows 10 version 1809 (build 17763) or later. The current project is tested as an x64 application.
2. [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). Install the SDK, not only the runtime.
3. The x64 Windows App Runtime matching Windows App SDK `2.4.0`, if it is not already present. The current downloads are on Microsoft's [Windows App SDK downloads page](https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads).

For Visual Studio development, use **Visual Studio Community 2026** and install its WinUI workload:

1. Open **Visual Studio Installer**.
2. Find **Visual Studio Community 2026** and select **Modify**.
3. Under **Workloads**, select **WinUI application development**.
4. Under **Individual components**, verify **Windows 11 SDK 10.0.26100** is selected.
5. Apply the changes and restart Visual Studio.

The WinUI workload and Windows 11 SDK are installed on the original development computer. They remain optional for the verified command-line build but are recommended for the full WinUI editing/debugging experience.

Windows Developer Mode is not required for this milestone because the application currently runs unpackaged. It may become necessary if a later distribution milestone moves to packaged/MSIX development.

The optional Microsoft CLI template pack is also not required to build this repository. Install it only if you want to create additional WinUI projects yourself:

```powershell
dotnet new install Microsoft.WindowsAppSDK.WinUI.CSharp.Templates
```

## Build, test, and run

Open PowerShell and change to the repository:

```powershell
Set-Location D:\Projects\VerityWorkbench
```

Confirm the SDK:

```powershell
dotnet --version
```

The expected version is `10.0.400` or a compatible later .NET 10 patch selected by `global.json`.

Restore packages. Internet access is required the first time:

```powershell
dotnet restore .\VerityWorkbench.sln --configfile .\NuGet.Config -p:Platform=x64
```

Build the complete solution:

```powershell
dotnet build .\VerityWorkbench.sln --configuration Debug --no-restore -p:Platform=x64
```

Run all unit and persistence tests:

```powershell
dotnet test .\VerityWorkbench.sln --configuration Debug --no-restore -p:Platform=x64
```

Run the Windows application after the build succeeds:

```powershell
dotnet run --project .\src\VerityWorkbench.App\VerityWorkbench.App.csproj --configuration Debug --no-build --no-restore -p:Platform=x64
```

Verified baseline at the time of this README update: the solution builds with zero warnings and zero errors; all 67 tests pass (12 core, 44 persistence, and 11 media-safety tests).

## Use the current application

1. Launch the application using the command above.
2. Select **Add Profile**.
3. Enter a pseudonymous profile name. Do not put identifying information in the display name.
4. Select a dedicated profile workspace folder. A drive root such as `D:\` is rejected.
5. Optionally select a separate download-staging folder. Leave it blank to use `Downloads` inside the workspace.
6. Add one or more explicitly selected local `.mp4` files to the appropriate training list.
7. Enter any recording-date text you want displayed with each video, then press **Enter** to accept it. This is an opaque display/sort label; it is not parsed as a date and is never a model feature, label, identity, or verified capture time.
8. Remove items or sort either list by the recording label as needed.
9. Select **Save draft**.

On success, the main view shows the profile as **Draft — not processed**. The profile reloads automatically after the app restarts. Saving a profile does not copy, change, open, or delete any source MP4.

Selecting **Cancel** before saving clears the form and creates no profile or workspace folders.

### Edit a saved profile

1. Select a profile row on the main view.
2. Select **Edit Profile**.
3. Change the pseudonymous name or any recording label, add explicit local MP4 selections, or sort a list.
4. Every row has **Remove**. A previously saved row also has **Archive** or **Unarchive**. Archiving here changes only local selection metadata; it does not move or delete the source MP4. Removing an unprocessed selection removes its current path/label row but never deletes the source MP4. SQLite secure deletion is enabled, but this is not a promise to purge external backups, filesystem snapshots, or storage-device history.
5. Select **Save changes** to commit the update atomically, or **Cancel** to discard the detached working copy.

Workspace and download roots are read-only during Edit in this milestone. Safe relocation behavior is still unresolved and will not be simulated by merely changing a stored path. Editing starts no background work. A profile remains **Media registered — awaiting validation** only when every active selection is already linked to an ingested asset; otherwise it returns to **Draft — not processed** until the new active selections are ingested.

If another app window saves the same profile after you opened it, your stale edit is refused and the list is refreshed; the later click does not silently overwrite the earlier save.

### Ingest local media

1. Select a saved profile that shows one or more items **awaiting ingest**.
2. Select **Process Data**.
3. VerityWorkbench copies each active, not-yet-ingested local MP4 into one bounded `Processing` job while computing SHA-256. The source file is opened read-only and is never modified.
4. Complete copies are atomically promoted beneath the profile's `Media` folder. The profile then shows **Media registered — awaiting validation**.
5. Selecting **Process Data** again on a fully registered profile rechecks every active workspace copy's byte length and SHA-256. This is an explicit integrity check, not MP4 stream validation.

Select **Cancel Processing** to request cooperative cancellation. The active stream is closed, no partial file is promoted, the source remains unchanged, and the unique processing-job folder is retained for inspection. If the app exits unexpectedly, a job with no heartbeat for ten minutes is marked interrupted the next time that profile is loaded; select **Refresh** after that grace period if the app is already open. A fresh job from another app window is not interrupted.

Before moving a complete staged copy into `Media`, the app writes a small promotion journal containing only stable IDs, hashes, byte lengths, and workspace-relative paths. It contains no original source path. On restart or Refresh, terminal and stale jobs use this journal to finish a committed move or return an uncommitted folder to its processing job without deleting the media bytes.

Content identity is the SHA-256 hash, not a path or filename. Identical content in the same training condition reuses one app-managed asset. The app verifies a stored asset's length and SHA-256 before reusing it and whenever **Process Data** is selected for a registered profile, but it cannot prevent another process or the user from changing workspace files. A missing or changed copy is reported as **Workspace media needs repair**; this milestone changes neither the damaged copy nor its metadata because a journaled, no-deletion repair workflow is not implemented yet. Keep the original source MP4. Identical bytes assigned to both sincere-truth and intentional-deception conditions are rejected for manual label resolution.

This milestone checks file integrity and the `.mp4` extension only. **Media registered** does not mean the container or streams have been validated. Do not delete the original source until a later FFmpeg-enabled release reports that the workspace copy passed media validation.

## Persistent local metadata

Each profile's authoritative metadata database is stored inside its selected workload:

```text
<profile-workspace>\Profile\profile.sqlite
```

That SQLite database contains stable pseudonymous IDs, display names, normalized workspace/download roots, full local source-file paths, recording labels, training buckets, archive/order metadata, media hashes and workspace-relative asset paths, ingest-job status/progress, readiness, and timestamps. It contains no video bytes, frames, extracted audio, transcripts, model features, or scores.

To find those user-selected workspaces after restart, the app keeps a minimal per-Windows-user locator catalog at:

```text
%LOCALAPPDATA%\VerityWorkbench\profile-catalog.sqlite
```

The catalog contains only each profile's stable ID, normalized workspace root, catalog timestamp, and pending/ready recovery state. It does not contain the profile display name, download root, video paths, recording labels, buckets, or archive state. Pending entries let startup finish or safely roll back profile creation interrupted between the two databases. Fresh pending work receives a ten-minute grace period so a second app window cannot mistake an active save for abandoned work. A missing, damaged, or malformed profile database is isolated so healthy catalog entries can still load; malformed catalog rows are skipped and counted rather than disabling the entire list.

Display names are checked for duplicates among profiles that are currently available, but the stable profile ID and workspace—not the editable display name—are the identity. The workspace path shown beside each name disambiguates profiles if an offline profile or concurrent app instance later introduces the same display name.

These milestone databases are local but are not additionally encrypted by VerityWorkbench. Protect the Windows account and disk—for example with appropriate access controls and BitLocker—because paths and labels can themselves be sensitive. Database contents are never included in a `.vwpkg` by default, uploaded, logged to telemetry, or committed to this repository.

## Training-label boundary

The two lists are user/adjudicator-supplied enrollment buckets:

- **Verified sincere-truth** means the target-subject material was produced under an independently supported sincere-truth condition.
- **Verified intentional-deception** means the target-subject material was produced under an independently supported intentional-deception condition.

Bucket placement is never model-generated ground truth. Factual status remains separate from intent. Mixed, ambiguous, off-condition, other-speaker, voice-over, or B-roll material must be segmented or excluded according to the design document. Multiple frames, transcript sentences, retakes, excerpts, and simultaneous camera views do not create independent training samples.

## Workspace layout

Saving a valid draft creates only these fixed, human-readable top-level folders beneath the selected workspace:

```text
<profile-workspace>/
  Profile/
    profile.sqlite
  Media/
  Downloads/
  Processing/
  Features/
  Models/
  Exports/
  Reports/
```

If a separate download-staging folder is selected, that folder is also created if necessary. Later download and processing work will be confined to unique, bounded job subfolders. Folder names and paths are for human navigation only and must never become model features.

Do not place a workspace inside the source repository. Subject media, workspaces, processing artifacts, transcripts, numeric biometric features, and person-specific models are private local data and must never be committed.

## Planned product workflow

### Add Profile

A complete Add Profile workflow will accept curated training videos, an imported `.vwpkg`, or both; ingest local MP4s or authorized direct HTTP(S) MP4 URLs; then create a cancellable processing job.

### Edit Profile

Edit Profile handles persistent metadata, additions, and archive/unarchive eligibility. Ingested selections cannot be removed as though they were still unprocessed; archive them to exclude them from future work. Future slices will add FFmpeg validation, reprocessing, verified deletion and root relocation, compatible package import, and eligible-model export. Any future training change will require a new processing/model version rather than silently mutating an accepted model.

### Query Profile

Query Profile will select a query-ready profile and a local/direct-URL MP4, process it locally, and show synchronized playback and timestamped transcript/answer/claim rows. Output granularity must match the trained and validated target granularity. No row will be labeled “% truth” or “truth confidence.”

## Media, privacy, and export boundaries

- Version 1 supports MP4 only.
- Inputs must be explicitly selected local files or user-supplied direct HTTP(S) MP4 media URLs.
- A future folder picker may display supported files for explicit review; it must not recursively or silently import a directory.
- There is no webpage scraping, playlist extraction, streaming-site support, cookie reuse, DRM circumvention, or media ripping.
- Processing is local. No account, hosted analysis service, or telemetry is required.
- Source files are never modified.
- Users are responsible for rights, consent, retention, label provenance, and authorized sharing.
- A cancellation request stops the active local-ingest operation, closes its file handles, promotes no partial artifact, and leaves the unique processing folder available for inspection or later deletion. Future multi-process workers must preserve this same boundary.

Every future export will produce exactly one encrypted `.vwpkg` file. Two planned types are:

- **Query-only model:** frozen compatible preprocessing/model/calibration artifacts for local queries; no historical training-state extension.
- **Trainable profile:** the query artifacts plus allowlisted numeric features, coded labels, grouping metadata, configuration, and sanitized provenance needed to retrain with new authorized videos.

Neither type will contain original videos or any media derivative. Excluded material includes playback proxies, extracted audio, frames, face crops, thumbnails, media fragments, raw transcripts, free-text claims/evidence, source URLs, original local paths, credentials, executables, scripts, and plugins.

## Repository structure

```text
VerityWorkbench/
  src/
    VerityWorkbench.App/       WinUI shell and Add/Edit Profile UI
    VerityWorkbench.Core/      Profile validation and workspace rules
    VerityWorkbench.Data/      Local SQLite profile persistence
    VerityWorkbench.Media/     Bounded local staging, hashing, and atomic promotion
  tests/
    VerityWorkbench.Core.Tests/
    VerityWorkbench.Data.Tests/
    VerityWorkbench.Media.Tests/
  design_doc.md                Authoritative design and research constraints
  README.md                    Setup, status, and usage guide
  VerityWorkbench.sln
```

Additional Jobs, Inference, and Packaging projects will be added only when a tested vertical slice needs them.

## Pinned development dependencies

| Dependency | Version | Purpose |
|---|---:|---|
| .NET SDK | 10.0.400 | Build and run the solution |
| Windows App SDK | 2.4.0 | WinUI 3 desktop application |
| Microsoft.Data.Sqlite | 10.0.10 | Persistent local profile metadata |
| SQLitePCLRaw.lib.e_sqlite3 | 2.1.12 | SQLite native runtime |
| xUnit | 2.9.3 | Core unit tests |
| xUnit Visual Studio runner | 3.1.4 | Test discovery and execution |
| Microsoft.NET.Test.Sdk | 17.14.1 | .NET test host |

No third-party media, ML, or packaging dependency has been added yet. Local staging and hashing use the .NET runtime.

## Project status and license

VerityWorkbench is a research prototype, not a validated diagnostic, forensic, safety, or decision-making system. Do not claim accuracy, production readiness, regulatory suitability, or scientific validation that has not been demonstrated prospectively.

The final open-source license has not yet been selected. Apache-2.0 remains the provisional direction in the design document; no license grant should be inferred until a `LICENSE` file is added.
