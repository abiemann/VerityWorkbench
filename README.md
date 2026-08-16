# VerityWorkbench

VerityWorkbench is a local-first Windows research workbench for investigating whether person-specific facial, vocal, timing, and linguistic changes correlate with independently supported intentional deception under controlled conditions.

> Early development — Milestone 4 MP4 media validation. The application creates and edits persistent profiles, copies explicitly selected local MP4s into user-selected app-managed workspace assets, records their integrity metadata, and uses a pinned FFmpeg/ffprobe toolchain to validate an unambiguous video-and-audio stream selection with a complete CPU decode. It does not yet create proxies, extract audio or behavioral features, train a model, verify identity or authenticity, recognize language, or produce any behavioral score or probability.

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
- [x] Version- and hash-pinned FFmpeg/ffprobe MP4 probing, normalized metadata persistence, and complete selected-stream CPU decode validation
- [ ] Media-quality thresholds, timestamp mapping, playback proxies, and audio extraction
- [ ] Direct-URL resumable downloads
- [ ] Processing-folder inspection and verified cleanup controls
- [ ] Playback, transcription, and feature extraction
- [ ] Local multilingual ASR, language confirmation, original-language transcripts, optional separate English translation, and compatible language-model routing
- [ ] Training, grouped validation, calibration, and local inference
- [ ] Query-time profile-subject identity gate and face-speaker verification
- [ ] Separate provenance and synthetic/manipulated-media assessment
- [ ] Query Profile workflow
- [ ] One-file encrypted `.vwpkg` import/export

**Query Profile** remains deliberately unavailable. Edit Profile updates profile metadata and media eligibility but does not relocate workspaces, delete ingested assets, import packages, or export models. Media validation establishes only that the selected MP4 streams satisfy this engineering contract and decode completely; it does not establish subject identity, media authenticity, spoken language, label correctness, or suitability for model training. No placeholder or random scoring code exists.

## What to install

### To test this slice on the current development computer

This computer has .NET SDK `10.0.400` and the separately installed media tools below:

```text
D:\Tools\VerityWorkbench\FFmpeg\8.1\bin\ffmpeg.exe
D:\Tools\VerityWorkbench\FFmpeg\8.1\bin\ffprobe.exe
```

The required build is BtbN FFmpeg-Builds `win64-lgpl-8.1`, build identity `n8.1.2-44-g7c533d0f86-20260815`, from release tag `autobuild-2026-08-15-13-02`. It reports `LGPL version 3 or later`; GPL and nonfree variants are not accepted by this validation contract.

Python, GPU drivers/toolkits, Whisper, and model files are **not needed** for this milestone. Validation deliberately uses the CPU/software decode path.

### On another Windows development computer

Install:

1. Windows 10 version 1809 (build 17763) or later. The current project is tested as an x64 application.
2. [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). Install the SDK, not only the runtime.
3. The x64 Windows App Runtime matching Windows App SDK `2.4.0`, if it is not already present. The current downloads are on Microsoft's [Windows App SDK downloads page](https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads).
4. The exact external FFmpeg/ffprobe build described in [Install and verify FFmpeg](#install-and-verify-ffmpeg). Do not substitute another build merely because it reports FFmpeg 8.1.

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

### Install and verify FFmpeg

FFmpeg is a separate local tool dependency and is not committed to this repository or bundled into the application. Download the `win64-lgpl-8.1` archive from the BtbN FFmpeg-Builds release tagged [`autobuild-2026-08-15-13-02`](https://github.com/BtbN/FFmpeg-Builds/releases/tag/autobuild-2026-08-15-13-02), then extract it so the two executables are directly below a `bin` folder. The current development root is:

```text
D:\Tools\VerityWorkbench\FFmpeg\8.1
```

Verify the build identity and file hashes in PowerShell:

```powershell
$verityFfmpegRoot = 'D:\Tools\VerityWorkbench\FFmpeg\8.1'
& "$verityFfmpegRoot\bin\ffmpeg.exe" -version | Select-Object -First 1
& "$verityFfmpegRoot\bin\ffprobe.exe" -version | Select-Object -First 1
(Get-FileHash -LiteralPath "$verityFfmpegRoot\bin\ffmpeg.exe" -Algorithm SHA256).Hash
(Get-FileHash -LiteralPath "$verityFfmpegRoot\bin\ffprobe.exe" -Algorithm SHA256).Hash
```

The first two lines must identify `n8.1.2-44-g7c533d0f86-20260815`. The expected hashes are:

```text
ffmpeg.exe  FE6FAEE813EF5B4407F10DB5C8F0CC50CEE0B0A1A981F0B903567E2EBB7B92DF
ffprobe.exe AAA354B9841D92B4FA5F60EAF58169055B5D9D3D0420AC553784523DFE312724
```

The app does not search `PATH`. Configure the directory containing `bin` in either of these ways:

- Copy `src\VerityWorkbench.App\appsettings.Local.example.json` to `src\VerityWorkbench.App\appsettings.Local.json`, edit `mediaTools.ffmpegRoot` if needed, and rebuild. `appsettings.Local.json` is intentionally ignored by Git.
- Before launching the app from a terminal, set `$env:VERITYWORKBENCH_FFMPEG_ROOT` to the absolute root. This environment variable takes precedence over the local settings file.

The tracked [`media-tools.manifest.json`](src/VerityWorkbench.App/media-tools.manifest.json) is the authoritative validation pin: it records the distribution, release tag, variant, exact build identity, declared `LGPL-3.0-or-later` license, executable names, executable SHA-256 hashes, and validation-contract version. At runtime the app independently hashes both executables and checks their reported identity before starting a validation job. A missing, modified, or different build is rejected before profile processing state changes. The manifest records provenance for reproducibility; it does not replace the FFmpeg license notices or any redistribution obligations.

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

The checked-in solution is expected to build with zero warnings and zero errors and to pass all tests. Use the current command output as the authoritative test count because the suite grows with each implementation slice.

## Use the current application

1. Launch the application using the command above.
2. Select **Add Profile**.
3. Enter a pseudonymous profile name. Do not put identifying information in the display name.
4. Select a dedicated profile workspace folder. A drive root such as `D:\` is rejected.
5. Optionally select a separate download-staging folder. Leave it blank to use `Downloads` inside the workspace.
6. Add one or more explicitly selected local `.mp4` files to the appropriate training list.
7. Enter any recording-date text you want displayed with each video, then press **Enter** to accept it and move to the next recording-label field when one is available. This is an opaque display/sort label; it is not parsed as a date and is never a model feature, label, identity, or verified capture time.
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

Workspace and download roots are read-only during Edit in this milestone. Safe relocation behavior is still unresolved and will not be simulated by merely changing a stored path. Editing starts no background work. Readiness is recalculated from the active selections and their immutable media state: adding or unarchiving an unprocessed selection returns the profile to **Draft — not processed**; a metadata-only edit preserves completed validation; archiving a failed item can make the remaining active set eligible again.

If another app window saves the same profile after you opened it, your stale edit is refused and the list is refreshed; the later click does not silently overwrite the earlier save.

### Ingest local media

1. Select a saved profile that shows one or more items **awaiting ingest**.
2. Select **Process Data**.
3. VerityWorkbench copies each active, not-yet-ingested local MP4 into one bounded `Processing` job while computing SHA-256. The source file is opened read-only and is never modified.
4. Complete copies are atomically promoted beneath the profile's `Media` folder. The profile then shows **Media registered — awaiting validation**.
5. This first **Process Data** pass stops after ingest. Select **Process Data** again to begin the separate validation stage described below.

Select **Cancel Processing** to request cooperative cancellation. The active stream is closed, no partial file is promoted, the source remains unchanged, and the unique processing-job folder is retained for inspection. If the app exits unexpectedly, a job with no heartbeat for ten minutes is marked interrupted the next time that profile is loaded; select **Refresh** after that grace period if the app is already open. A fresh job from another app window is not interrupted.

Before moving a complete staged copy into `Media`, the app writes a small promotion journal containing only stable IDs, hashes, byte lengths, and workspace-relative paths. It contains no original source path. On restart or Refresh, terminal and stale jobs use this journal to finish a committed move or return an uncommitted folder to its processing job without deleting the media bytes.

Content identity is the SHA-256 hash, not a path or filename. Identical content in the same training condition reuses one app-managed asset. The app verifies a stored asset's length and SHA-256 before reusing it and whenever **Process Data** is selected for a registered profile, but it cannot prevent another process or the user from changing workspace files. A missing or changed copy is persistently marked **Workspace media changed — repair required**, which blocks further processing after a restart while preserving the prior immutable validation record for audit. This milestone does not alter, delete, or replace the damaged copy. Archiving every training selection linked to it only excludes that asset so the remaining active set can proceed; it does not repair the asset, and adding the same media again is not a replacement workflow. Retain the original source until a journaled repair workflow is implemented. Identical bytes assigned to both sincere-truth and intentional-deception conditions are rejected for manual label resolution.

**Media registered** means only that the app-managed bytes passed the ingest integrity checks. It does not mean the container or streams have been validated. Keep the original source at least until the second processing stage reports **Media validated — awaiting feature extraction**.

### Validate registered MP4 media

1. Select a profile showing **Media registered — awaiting validation** or **Media validation needs attention**.
2. Select **Process Data**. Before creating a job, VerityWorkbench loads the local media-tool configuration, hashes both executables, and verifies the exact pinned FFmpeg/ffprobe identity. Dependency failure leaves the profile in its previous safe state.
3. The app rechecks each active workspace copy's expected byte length and SHA-256, then invokes `ffprobe` directly without a shell. The MP4 must have a supported MP4 container and an unambiguous usable stream choice.
4. Validation requires exactly one selected usable video stream and one selected usable audio stream. Missing, invalid, or ambiguous choices fail safely. Metadata such as codec, dimensions, duration, frame-rate rational, sample rate, channel count, stream indices, and tool provenance is normalized before persistence; raw probe JSON, raw tool errors, executable paths, and original source paths are not stored as validation results.
5. The selected streams are completely decoded to a null output by the pinned `ffmpeg` executable with hardware acceleration disabled. This is a CPU/software integrity pass over the full selected audio and video streams, not a sample-only header check. The app creates no proxy, extracted audio, frames, thumbnails, transcript, or other media derivative.
6. The app rechecks the media length and SHA-256 after decoding. Only a stable file that passes the entire contract is marked **Media validated — awaiting feature extraction**. A content rejection is recorded as a bounded, sanitized failure and the profile shows **Media validation needs attention**. A missing or changed registered copy is instead persisted as **Workspace media changed — repair required** and cannot be revalidated until it is repaired or excluded from the active training set.

External processes have fixed time and output limits. Select **Cancel Processing** to stop the active validation: the app terminates the child process tree, waits for exit, closes process and media handles, and restores a safe derived profile state without writing a success result. A later **Process Data** attempt can retry failed or interrupted validation; an already successful immutable result is not silently replaced.

This engineering gate says only that the registered MP4, selected stream metadata, and complete CPU decode passed the pinned contract. It does **not** assess recording quality for modeling, confirm the profile subject, associate face and speaker, detect synthetic or manipulated media, recognize or compare spoken language, validate the user's training label, extract features, or produce a score, probability, or statement about truth.

## Persistent local metadata

Each profile's authoritative metadata database is stored inside its selected workload:

```text
<profile-workspace>\Profile\profile.sqlite
```

That SQLite database contains stable pseudonymous IDs, display names, normalized workspace/download roots, full local source-file paths, recording labels, training buckets, archive/order metadata, media hashes and workspace-relative asset paths, ingest/validation job status and progress, normalized successful validation metadata and tool provenance, bounded sanitized validation or integrity-failure states, readiness, and timestamps. It contains no video bytes, raw probe JSON, raw FFmpeg output, frames, extracted audio, transcripts, model features, or scores.

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

Edit Profile handles persistent metadata, additions, and archive/unarchive eligibility. Ingested selections cannot be removed as though they were still unprocessed; archive them to exclude them from future work. Future slices will add feature extraction/reprocessing, verified deletion and root relocation, compatible package import, and eligible-model export. Any future training change will require a new processing/model version rather than silently mutating an accepted model.

### Query Profile

Query Profile will select a query-ready profile and a local/direct-URL MP4, process it locally, and show synchronized playback and timestamped transcript/answer/claim rows. Before any behavioral result is produced, a dedicated one-to-one gate must verify the selected face track, establish face-speaker association, and verify voice biometrics only when the frozen profile policy requires them. The behavioral model will never double as the identity verifier.

Non-English audio will be transcribed locally using a multilingual ASR model. The application proposes a language, shows confidence/coverage, and lets the user confirm or correct it. A correction is audited and reruns model routing, but it cannot override inadequate target-speech coverage or unsupported code-switching. The original-language transcript remains authoritative; a human correction and optional English translation are stored and displayed separately. The UI preserves Unicode original script and supports right-to-left and mixed-direction text without forcing transliteration. The initial behavioral text pipeline uses immutable raw ASR tokens/timestamps; corrected and translated text remain presentation/evidence artifacts unless a separately controlled pipeline is trained and validated.

Each initial behavioral model version declares one canonical BCP 47 tag and an explicit validated compatible-tag policy; there is no silent locale or base-language fallback. A profile can hold an independently validated active model for each supported language. The UI reports language evidence as **Confirmed language: `{tag}`**, **Unable to determine spoken language**, or **Code-switched speech is not supported for behavioral scoring**. It separately reports routing as **Using active `{tag}` model**, **No active behavioral model for `{tag}`**, or **Required language dependency unavailable**. Only the first evidence/routing combination can be scored; the video may otherwise still be transcribed or translated. Training from several languages creates separate model candidates rather than silently pooling them.

The UI keeps selection, face identity, identity continuity, and speaker association separate. Multiple-person video first shows **Choose the profile subject**. Face identity is **Matches profile subject for analysis**, **Does not match profile subject**, or **Unable to verify profile subject**; continuity can be **Mixed or changing subjects**; speaker association is **Associated**, **Different speaker**, or **Unable to verify speaker association**. A query-level failure explains **This video cannot be evaluated with the selected profile**; a row-level failure explains **This segment was not scored**. Any non-passing required gate blocks the behavioral/deception output for the affected material. Missing/incompatible identity artifacts make the profile non-query-ready, while a runtime verifier failure is an analysis error rather than an identity non-match. The interface will not call a non-match “not real”: a genuine video of another person can fail the gate, and manipulated media depicting the enrolled subject may still pass it.

Media authenticity will therefore be a separate result: **Not assessed**, **No supported manipulation detected**, **Possible synthetic or manipulated media**, or **Inconclusive**. Possible manipulation always blocks behavioral scoring. Not-assessed or inconclusive results continue only when the frozen package policy is **Informational**; a **Required** policy blocks them. A negative manipulation screen will not be described as proof of authenticity.

For an eligible query, the initial analog display will be an uncalibrated directional experimental score from **More consistent with verified sincere-truth examples** to **More consistent with verified intentional-deception examples**, without a percent sign or probability-like 0–100 scale. It is unitless, model-version-specific, not linearly interpretable, and not comparable across profiles or versions. A percentage may appear only after the independent prospective calibration requirements in the design document pass, and then only as an **Estimated probability of intentional deception under this profile, protocol, and context**. It is never a factual truth percentage. Output granularity must match the trained and validated target granularity.

## Media, privacy, and export boundaries

- Version 1 supports MP4 only.
- Inputs must be explicitly selected local files or user-supplied direct HTTP(S) MP4 media URLs.
- A future folder picker may display supported files for explicit review; it must not recursively or silently import a directory.
- There is no webpage scraping, playlist extraction, streaming-site support, cookie reuse, DRM circumvention, or media ripping.
- Processing is local. No account, hosted analysis service, or telemetry is required.
- Source files are never modified.
- Users are responsible for rights, consent, retention, label provenance, and authorized sharing.
- A cancellation request stops the active ingest or media-validation operation, terminates any active FFmpeg/ffprobe process tree, closes file/process handles, writes no false success, promotes no partial artifact, and leaves the unique processing folder available for inspection or later deletion. Future workers must preserve this same boundary.

Every future export will produce exactly one encrypted `.vwpkg` file. Two planned types are:

- **Query-only model:** frozen compatible preprocessing/model/calibration artifacts plus the canonical model language, explicit compatible-tag policy, multilingual ASR/tokenization/raw-text provenance and language-routing contract, minimum numeric identity templates, gate policy, and portable verifier contract required for local queries; no historical training-state extension. Import fails if a required language or identity dependency is unavailable.
- **Trainable profile:** the query artifacts plus allowlisted numeric features, coded labels, grouping metadata, configuration, and sanitized provenance needed to retrain with new authorized videos.

Neither type will contain original videos or any media derivative. Excluded material includes playback proxies, extracted audio, frames, face crops, thumbnails, media fragments, original/corrected/translated transcript text, free-text claims/evidence, source URLs, original local paths, credentials, executables, scripts, and plugins. Numeric identity templates remain sensitive biometric derived data; the export review must identify them explicitly, require authorization, and keep them inside the encrypted package.

## Repository structure

```text
VerityWorkbench/
  src/
    VerityWorkbench.App/       WinUI shell and Add/Edit Profile UI
    VerityWorkbench.Core/      Profile validation and workspace rules
    VerityWorkbench.Data/      Local SQLite profile persistence
    VerityWorkbench.Media/     Bounded staging, hashing, promotion, probing, and full-decode validation
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
| BtbN FFmpeg/ffprobe | n8.1.2-44-g7c533d0f86-20260815 (`win64-lgpl-8.1`) | MP4 probing and selected-stream CPU decode validation |
| xUnit | 2.9.3 | Core unit tests |
| xUnit Visual Studio runner | 3.1.4 | Test discovery and execution |
| Microsoft.NET.Test.Sdk | 17.14.1 | .NET test host |

FFmpeg/ffprobe is invoked as a separately installed, pinned external LGPL toolchain; it is not copied into this repository. No third-party ML or packaging dependency has been added yet. Local staging and hashing use the .NET runtime.

## Project status and license

VerityWorkbench is a research prototype, not a validated diagnostic, forensic, safety, or decision-making system. Do not claim accuracy, production readiness, regulatory suitability, or scientific validation that has not been demonstrated prospectively.

The final open-source license has not yet been selected. Apache-2.0 remains the provisional direction in the design document; no license grant should be inferred until a `LICENSE` file is added.
