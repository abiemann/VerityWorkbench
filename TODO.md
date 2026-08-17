# VerityWorkbench TODO

## Milestone 4 MP4 media validation

- [x] Pin the external BtbN `win64-lgpl-8.1` FFmpeg/ffprobe build `n8.1.2-44-g7c533d0f86-20260815`, its release provenance, declared license, executable names, SHA-256 hashes, and validation-contract version in a tracked manifest; keep the absolute local installation root out of Git.
- [x] Load the FFmpeg root from `VERITYWORKBENCH_FFMPEG_ROOT` or an ignored `appsettings.Local.json`; use explicit executable paths and never search `PATH` or invoke a shell.
- [x] Verify both executable hashes and reported build identities before starting a validation job, with bounded execution time and standard-output/error capture.
- [x] Recheck each registered media asset's expected byte length and SHA-256 immediately before and after validation.
- [x] Probe MP4 structure into bounded JSON, require an unambiguous usable video-stream selection and audio-stream selection, and normalize only the required container, timing, codec, video, and audio fields.
- [x] Completely decode exactly one selected video stream and one selected audio stream to null output on the CPU/software path; create no media derivative.
- [x] Persist immutable successful normalized validation results and tool provenance without executable paths, original media paths, raw probe JSON, or raw FFmpeg/ffprobe errors; retain only bounded sanitized failure categories/messages.
- [x] Track validation as a separate persistent processing job with snapshotted asset membership, heartbeat/progress, retryable content failures, safe stale-job recovery, and readiness derived from active media state.
- [x] On cancellation or operational failure, terminate the external process tree, close handles, write no successful result, and restore the profile's safe derived pre-job readiness.
- [x] Persist a registered-media integrity-failure state that survives restart, blocks further processing, preserves immutable validation provenance, and can be excluded by archiving the affected training selection.
- [ ] Add a journaled, no-deletion repair workflow that can replace a changed or missing registered media copy from an explicitly selected source.
- [ ] Add inspection and verified cleanup controls for retained processing-job folders and future media derivatives.
- [ ] Add automated integration fixtures for additional supported codecs, corrupt/truncated MP4s, ambiguous/default stream combinations, cancellation during long decode, timeout/output-limit enforcement, and post-decode mutation detection.

## Milestone 5 deterministic media preprocessing

- [x] Freeze and hash a versioned preprocessing contract tied to the pinned FFmpeg build and validated-media contract; require the same existing local FFmpeg/ffprobe installation and no additional dependency.
- [x] Generate an immutable playback/presentation-only `proxy.mp4` using the CPU/software MPEG-4 Part 2 encoder, `yuv420p`, aspect-ratio-preserving dimensions no larger than 1280×720, a 30 fps target, and stereo 48 kHz AAC.
- [x] Generate a mono 16 kHz signed 16-bit little-endian PCM `audio.wav` for later analysis work.
- [x] Generate a versioned affine target-time `timestamp-map.json` that records the first-decoded source presentation-time origin and explicitly states that the map is not exact source-frame lineage, 30 fps conversion can select/duplicate/omit frames, asynchronous resampling can pad/trim/compensate discontinuities, and time/sample conversion is rounded.
- [x] Generate a privacy-bounded `preprocessing-manifest.json` without source paths, filenames, raw tool output, transcripts, features, or scores; persist exact SHA-256 hashes and byte lengths for the proxy, WAV, timestamp map, and manifest.
- [x] Stage the complete four-file bundle inside one bounded preprocessing job and atomically promote it to `Media/<asset>/Prepared/v1_<first-12-contract-hash>/` without overwriting an existing immutable contract bundle.
- [x] Persist preprocessing jobs, immutable successful results, artifact/tool provenance, per-asset retryable failures, and profile readiness across restart.
- [x] Keep ingest, validation, and preprocessing as distinct phases: one **Process Data** click advances at most one phase.
- [x] On cancellation or operational failure, terminate the external process tree, close handles, write no false success, promote no partial bundle, retain the processing folder, and restore safe derived readiness.
- [x] Reconcile preprocessing promotion journals on Refresh/startup before stale-job recovery; verify committed bundles, return uncommitted bundles to their job when safe, and persist integrity failure for inconsistent committed artifacts.
- [x] Record media quality and model applicability as **Not assessed**; do not infer feature, training, identity, authenticity, language, or behavioral readiness from successful preprocessing.
- [x] Keep the playback proxy outside the approved visual-model input contract and exclude every media derivative from both query-only and trainable `.vwpkg` exports.
- [ ] Add declared, prospectively justified media-quality and model-applicability thresholds before feature extraction can treat prepared media as eligible.
- [ ] Add a separately frozen visual-analysis input/frame-sampling contract; do not silently use the playback proxy as model input.
- [ ] Before introducing preprocessing contract v2, add version-dispatched verification/migration so an intact historical v1 bundle is reported as compatible or reprocessing-needed, never as damaged merely because the current contract changed.
- [ ] Add automated long-running cancellation, forced-restart journal, tampered-committed-bundle, and representative real-media fixtures beyond the current generated installed-tool integration coverage.

## Milestone 6 prepared-media review and playback

- [x] Add a **Review Prepared Media** action for profiles with accepted prepared assets; keep **Query Profile** unavailable.
- [x] Load each unique media asset once and aggregate every linked training condition and recording label for display without treating repeated selections as independent media.
- [x] Before assigning a player source, recheck the registered original's stored byte length and SHA-256 and verify the complete accepted prepared bundle (`proxy.mp4`, `audio.wav`, `timestamp-map.json`, and `preprocessing-manifest.json`) against its stored paths, lengths, and hashes.
- [x] Use only the verified `proxy.mp4` for presentation playback; never use it as an approved visual-analysis input or silently fall back to `original.mp4`.
- [x] Provide play, pause, and seek controls with the current proxy target time and an explicitly labeled **Approximate source PTS** computed from the immutable v1 preprocessing source-timeline origin plus target time; never describe it as frame-accurate source lineage.
- [x] Refuse playback and use the existing safe integrity-failure path when the original or any required prepared artifact is missing, changed, or outside the expected workspace boundary.
- [x] Keep `ProfileReadiness.MediaPrepared`, `MediaQualityState.NotAssessed`, and `ModelApplicabilityState.NotAssessed` unchanged before, during, and after review.
- [ ] Add focused tests for unique-asset aggregation, complete pre-playback verification, affine time display, path containment, and state preservation across close/reopen and restart.
- [x] Perform the README manual test on prepared media and confirm that review creates no transcript, feature, identity/language/quality/applicability result, model, score, or percentage.

## Milestone 7 recording dependency groups

- [x] Add stable profile-scoped recording dependency-group IDs with editable, locally unique display labels.
- [x] Let Add/Edit Profile explicitly assign each training selection to one group; never infer an assignment from the opaque recording-date label.
- [x] Migrate every existing training-selection row to **Unassigned** rather than guessing at historical capture-event relationships.
- [x] Allow one group to span both training conditions because the group expresses dependence, not the sincere-truth/intentional-deception label.
- [x] Preserve archived rows' group assignments for audit while excluding archived rows from every active group, unassigned, and conflict count.
- [x] Detect active shared-media-asset conflicts when selections linked to one media asset are assigned to different dependency groups.
- [x] Summarize only active assigned dependency-group, unassigned-selection, and shared-asset-conflict counts; never present any of them as an independent-session or effective-sample-size count.
- [x] Define future training preflight to remain blocked while an active selection is unassigned or an active shared-asset group conflict exists.
- [x] Keep grouping metadata-only: do not change processing jobs, profile readiness, prepared-media review, **Query Profile**, media quality/applicability, or create any analysis artifact.
- [x] Require no new runtime, model, worker, or manual installation for this milestone.
- [ ] Perform the README manual test for creation, rename stability, explicit assignment, cross-condition membership, migration, archive/unarchive counts, shared-asset conflicts, restart persistence, and unchanged processing/readiness/review/query state.

## Multilingual transcription and model-language gate

- [ ] Select and license a local multilingual ASR model/runtime; do not use an English-only model for the product path.
- [ ] Detect a BCP 47 spoken-language tag from usable target-subject speech, show confidence/coverage, and require an audited user confirmation or correction; a correction reroutes the query but cannot override inadequate coverage or unsupported code-switching.
- [ ] Preserve original ASR, human-corrected original-language text, and optional English translation as separate private artifacts.
- [ ] Preserve Unicode original script and punctuation; support right-to-left and mixed-direction transcript editing, seeking, and rendering without forced transliteration.
- [ ] Use immutable raw ASR tokens/timestamps for the initial behavioral text pipeline; keep corrections and translation out unless an outcome-blinded, versioned correction/translation pipeline is separately trained, ablated, and validated.
- [ ] Freeze one canonical BCP 47 tag plus an explicit compatible-tag allowlist/range per initial behavioral model; default to exact tag only with no implicit locale/base-language fallback.
- [ ] Require all fitting, selection, calibration, and evaluation material for one initial model version to satisfy that same frozen language policy.
- [ ] Reject language/outcome confounding; truthful, intentional-deception, and control conditions must not differ systematically by language.
- [ ] Store language evidence separately from model routing: confirmed tag / unable to determine / unsupported code-switching, then active compatible model / no active model / dependency unavailable.
- [ ] Permit behavioral scoring only when adequately covered confirmed language evidence routes to a compatible active model; all other states block scoring but not transcription/translation.
- [ ] Build and promote separate immutable model candidates when one profile has authorized training material in multiple languages; maintain an independent active model pointer per confirmed language.
- [ ] Treat code-switched query speech as ineligible for initial behavioral scoring unless segment-level language boundaries and matching-language inference are separately validated.
- [ ] Create qualified independent reference language/transcript/code-switch annotations and use disjoint threshold-development and locked capture-group evaluation sets with similar-language/dialect and code-switch hard negatives.
- [ ] Validate language identification, original-language WER/CER, timestamp alignment, exact-tag/allowlist/range routing, code-switch behavior, user overrides, and end-to-end abstention with clustered confidence bounds for each supported language.
- [ ] Audit language ID confidence/coverage, indeterminate/code-switch state, ASR/alignment error, overrides/corrections, and text missingness by outcome; add a language-pipeline-metadata-only leakage baseline.
- [ ] Include the canonical model tag, compatible-tag policy, multilingual ASR/tokenizer/raw-text provenance versions, confidence/coverage and code-switch rules, and language-routing test vectors in `.vwpkg` compatibility metadata.

## Query profile identity and authenticity gates

- [ ] Implement a dedicated one-to-one profile-subject verifier that is independent of truth/deception labels and the behavioral model.
- [ ] Build face and optional voice enrollment templates only from independently confirmed subject sessions, with review for mixed-person or contaminated material.
- [ ] Select and license the verifier/model weights before real enrollment; encrypt templates at rest and implement consent withdrawal and verified deletion.
- [ ] Keep selection, face identity (`Match`/`NonMatch`/`Indeterminate`), identity continuity, speaker association, optional voice identity, and verifier capability/readiness as separate fields.
- [ ] Show **Choose the profile subject** before verification when multiple people appear; treat missing/incompatible verifier artifacts as non-query-ready and worker failure as an analysis error, not a biometric decision.
- [ ] Block all behavioral/deception output for every affected segment unless the required identity, face-speaker association, and continuity gates pass.
- [ ] Use separate conservative match and non-match thresholds with an uncertainty/abstention band; poor quality must not be reported as a mismatch.
- [ ] Associate face tracks with speaker turns so another person, voice-over, dubbing, or face/voice conflict is never scored under the selected profile.
- [ ] Keep identity non-match separate from media authenticity. Never label a wrong-person result **not real**.
- [ ] Add an independently evaluated provenance/manipulation result: not assessed, no supported manipulation detected, possible synthetic/manipulated media, or inconclusive. Freeze an Informational/Required policy; possible manipulation always blocks, while not-assessed/inconclusive block whenever the policy is Required. A negative screen must not claim authenticity.
- [ ] Validate on disjoint enrollment, threshold-development, and locked evaluation sets with complete held-out genuine sessions and representative/hard impostors; report false-match, false-non-match, hard genuine non-match, genuine/impostor indeterminate, failure-to-acquire, modality-conflict, and wrong-subject score-leakage rates with clustered confidence bounds.
- [ ] When voice biometrics are required, validate every supported enrollment-language/query-language pair or use separately validated language-specific voice templates.
- [ ] Validate authenticity detectors separately with bona-fide false alerts, attack misses, inconclusive coverage, held-out generators/tools, codecs, recompression, post-processing, and attack-specific uncertainty.
- [ ] Package only the minimum encrypted numeric identity templates, verifier contract, modality policy, thresholds, and validation metadata needed for query gating; include either an allowlisted portable verifier or the exact ID/hash of a required app-bundled verifier, and never package face crops, voice recordings, or other media.
- [ ] Show an uncalibrated analog behavioral direction without `%` or a 0–100 scale; freeze its transform/orientation and state that it is unitless, model-version-specific, non-linear, and not comparable across profiles/versions. Permit a percentage only after prospective calibration, and never label it factual truth probability.

## Future Android query application

- [ ] Keep the query-only `.vwpkg` format platform-neutral, including the model, preprocessing contract, feature-version metadata, calibration context, canonical/compatible language tags, ASR/tokenizer/raw-text provenance and routing contract, numeric identity templates, verifier contract, identity-gate policy, compatibility requirements, and integrity information.
- [ ] Build an Android query application only after Windows training and query inference are working and validated.
- [ ] Support importing a compatible query-only `.vwpkg` and evaluating locally captured or selected camera video on-device where practical.
- [ ] Preserve the exact Windows identity taxonomy, modality requirements, threshold policy, matched-context checks, media-quality/applicability reporting, uncertainty, abstention, and no claim of factual truth.
- [ ] Verify preprocessing, identity decisions, and behavioral inference parity between Windows and Android using identical reference media before releasing Android query functionality.
- [ ] Verify multilingual ASR, BCP 47 normalization, language-gate decisions, original-script rendering, and optional translation parity between Windows and Android.
- [ ] Treat live-camera liveness/presentation-attack checks separately from offline identity and media-forensics checks; selected gallery video cannot prove liveness.
- [ ] Never silently fall back to face-only or voice-only verification when the imported package requires both modalities.
- [ ] Keep subject media, identity templates, and derived biometric data encrypted and local by default; do not require cloud processing for the Android query path.
