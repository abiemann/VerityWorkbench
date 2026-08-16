# VerityWorkbench: Person-Specific Multimodal Veracity Research Workbench

**Project status:** Concept and research blueprint

**Research snapshot date:** August 15, 2026

**Product name:** VerityWorkbench

**Proposed platform:** Local-first Windows desktop application in C#/.NET

**Distribution direction:** Open-source software with private, user-supplied media and person-specific artifacts

**Initial approved media inputs:** Explicitly selected local MP4 files or user-supplied direct HTTP(S) MP4 media URLs that are downloaded and finalized locally before processing

**Working description:** A person-specific system for studying whether synchronized facial, vocal, and linguistic changes are associated with independently verified intentional deception under controlled, comparable conditions.

## 1. Executive summary

The proposed software is technically feasible as a research application. It can ingest an MP4, isolate a selected subject, extract synchronized facial, vocal, and transcript features, compare those features with a subject-specific model, and display results at the model's trained and validated granularity.

The Windows application centers on three actions: **Add Profile**, **Edit Profile**, and **Query Profile**. A profile represents one pseudonymous subject, that subject's authorized training artifacts, processing workspace, model history, and current query eligibility. A profile may be created from curated training videos, an imported model package, or both.

The application follows a **bring-your-own-video, local-only** boundary. It does not provide, discover, scrape, or bundle subject footage. Every newly added training or future-analysis video must enter as an explicitly selected local MP4 or a user-supplied direct MP4 media URL that the application downloads to a local private workspace. The public source repository contains no real subject footage, extracted biometric data, consent records, or person-specific model packages.

It is not presently scientifically defensible to market the application as a general-purpose lie detector or to assume that every person has a stable detectable “tell.” The system must first test whether a repeatable person-specific signal exists and whether it survives completely new recording sessions. Some subjects may be modelable under a narrow protocol; others may have no reliable signal.

The strongest defensible product has three deliberately separate outputs:

1. **Behavioral deviation:** How unusual the subject’s current facial and vocal behavior is relative to verified personal baselines.
2. **Experimental deception-model score:** A model result available only after training on both verified truthful and verified intentional-deception examples and validating on independent future sessions.
3. **Claim-evidence assessment:** Whether external evidence supports, contradicts, or cannot resolve each spoken factual claim.

A percentage must never be created directly from a neural-network confidence score. It may be displayed only after prospective, target-condition calibration demonstrates that the percentage has empirical meaning. Otherwise, the application must display an experimental score, a deviation score, or **cannot determine**.

This is therefore best framed as a **multimodal veracity research workbench**, not a consumer lie detector.

## 2. The exact concepts must remain separate

“Truthfulness” can refer to different things:

- **Factual accuracy:** Is the proposition correct in the outside world?
- **Speaker belief:** Does the speaker sincerely believe the proposition?
- **Intentional deception:** Is the speaker knowingly attempting to create a false belief?

These are not interchangeable. A person can sincerely state something false. A person can also make a technically true statement while intentionally creating a misleading impression through omission or framing.

Facial and vocal behavior cannot establish factual truth by themselves. At most, those modalities may contain behavioral correlates of an experimentally defined state such as intentional deception. Factual accuracy is better assessed through the transcript and external evidence.

The primary scientific target for the personalized behavioral model should be:

> Given subject S and a sufficiently comparable context C, do synchronized facial, vocal, timing, and linguistic changes predict independently verified intentional deception better than chance in a completely future recording session?

The desired statistical quantity, if validation eventually supports it, is not a universal probability. It is:

> P(intentional deception | measured features, this subject, this protocol, this context)

That qualification must remain visible in the model card and user interface.

## 3. Scientific findings reviewed

### 3.1 Human and behavioral deception detection

- Human truth/lie judgments average approximately 54% across 206 reports and 24,483 judges. Experts were not reliably superior in controlled comparisons. [Bond and DePaulo](https://pubmed.ncbi.nlm.nih.gov/16859438/)
- A major meta-analysis covering 1,338 estimates of 158 behavioral cues found that many behaviors had no discernible relationship with deception and that the typical effects were weak. The median absolute effect among well-studied cues was approximately d = 0.10. Eye contact, response latency, and response length were near zero in that analysis. [DePaulo et al.](https://doi.org/10.1037/0033-2909.129.1.74)
- Further meta-analytic work concluded that deception-judgment accuracy is limited mainly by the weakness of behavioral signals, not merely by poorly trained observers. [Hartwig and Bond](https://pubmed.ncbi.nlm.nih.gov/21707129/)
- A broad review described nonverbal deception cues as faint and unreliable. [Vrij, Hartwig, and Granhag](https://doi.org/10.1146/annurev-psych-010418-103135)

### 3.2 Faces, expressions, and microexpressions

- Facial movements do not uniquely and reliably identify even a particular emotion across people, cultures, situations, and contexts. The further inference from facial movement to emotion to deception is therefore especially uncertain. [Barrett et al.](https://pubmed.ncbi.nlm.nih.gov/31313636/)
- In a randomized study, microexpression training did not improve lie detection; overall performance was slightly below chance. [Jordan et al.](https://doi.org/10.1002/jip.1532)
- A critical review notes that microexpressions are rare, can occur in both truthful and deceptive people, and should not be treated as a validated lie marker. [Burgoon](https://doi.org/10.3389/fpsyg.2018.01672)
- A recent cross-database study found that facial deception models that performed promisingly within one dataset frequently deteriorated sharply, sometimes below chance, on another dataset. Microexpression models could approach roughly 80% within a dataset yet fail to exceed random performance across databases. [Cross-database facial study](https://doi.org/10.1007/s00521-024-09811-x)

### 3.3 Personalized baselines

Knowing a person’s truthful baseline may provide a small benefit in some controlled studies, but findings are inconsistent. One meta-analytic comparison found roughly 55.9% accuracy with baseline exposure versus 52.3% without it. Other controlled work found null or inconsistent improvements, especially when the baseline topic and stakes were not comparable to the questioned material. [Verigin et al.](https://pmc.ncbi.nlm.nih.gov/articles/PMC8451647/), [Bogaard et al.](https://doi.org/10.1002/acp.3990), [Baseline comparability study](https://pmc.ncbi.nlm.nih.gov/articles/PMC6762160/)

Person-specific signals can appear under constrained conditions:

- A facial-electromyography study with 40 participants and many repeated trials reported approximately 73% average within-person accuracy. The informative muscle differed by person, and some participants’ apparent signal changed over time. [Person-specific facial EMG study](https://pmc.ncbi.nlm.nih.gov/articles/PMC8671780/)
- A webcam-plus-smartwatch experiment reported approximately 75–87% within-person accuracy, but it included only four young male participants, used temporally related samples, and documented false positives from ambiguity and lingering stress. It does not validate ordinary natural video. [Webcam and smartwatch study](https://pmc.ncbi.nlm.nih.gov/articles/PMC10141812/)

These results support investigating personalization, but not assuming that every person has a stable tell.

### 3.4 Audio and speech

Audio adds useful measurements: pitch, intensity, pauses, speech rate, voice quality, response latency, turn-taking, and the spoken words themselves. However, vocal stress reflects arousal and cognitive load, not deception uniquely.

- Controlled testing of layered voice analysis found approximately chance-level detection, with false-positive rates reported between 40% and 65%. [Harnsberger et al.](https://pubmed.ncbi.nlm.nih.gov/19432740/)
- A meta-analysis of computer-detected linguistic cues found theory-consistent differences in some areas, but the overall effect was small and moderated by event type, involvement, emotional valence, motivation, and interaction conditions. [Hauch et al.](https://pubmed.ncbi.nlm.nih.gov/25387767/)
- UNIDECOR experiments across 14 textual deception corpora found that linguistic cues and models generalized poorly across dissimilar domains. [UNIDECOR](https://aclanthology.org/2023.wassa-1.5/)
- Acoustic and lexical features can predict whether humans perceive a speaker as trustworthy without reliably predicting actual deception. In one controlled study, human deception judgments were approximately 50% correct. [Chen et al.](https://aclanthology.org/2020.tacl-1.14/)

Audio is therefore valuable in two distinct ways:

1. It strengthens a personal behavioral-deviation model.
2. Its transcript enables claim extraction, consistency analysis, and evidence checking.

The second use is more directly related to factual correctness. It still does not prove the speaker’s intent.

### 3.5 Multimodal machine learning

Combining audio, video, and text can improve results inside a particular dataset, but the improvement is inconsistent and often disappears under domain shift.

- The DOLOS study reported approximately 59.2% accuracy for audio, 61.4% for video, and 66.8% for its strongest audiovisual configuration under within-dataset evaluation. Cross-dataset results were generally much lower. [DOLOS](https://openaccess.thecvf.com/content/ICCV2023/html/Guo_Audio-Visual_Deception_Detection_DOLOS_Dataset_and_Parameter-Efficient_Crossmodal_Learning_ICCV_2023_paper.html)
- A cross-domain benchmark spanning multiple audiovisual deception datasets reported best average fusion accuracy of 56.82% for single-source transfer, 58.88% for multi-source transfer, and 59.02% for its proposed domain-generalization method. Audio-only cross-domain variants were commonly near 51–53%. [Cross-domain audiovisual benchmark](https://arxiv.org/html/2405.06995)
- A 2025 multimodal evaluation found inconsistent modality gains. On one dataset, text alone outperformed audio, video, and all-modal models; several audio/video language models were near chance. [ACL 2025 multimodal evaluation](https://aclanthology.org/2025.acl-long.1497/)
- A registered replication of high-stakes public-plea behavioral cues found that some individual associations repeated, but the combined predictive model did not exceed chance in the replication sample. [Registered replication](https://pubmed.ncbi.nlm.nih.gov/40146559/)

This evidence makes context matching, independent-session testing, and abstention essential.

### 3.6 Why truthful-only training cannot produce a truth probability

Truthful examples for subject S estimate only:

> p(features | truthful, S)

They do not estimate:

> p(features | intentional deception, S)

They also do not supply the relevant truth/deception base rate. Infinitely many possible deception distributions are compatible with the same truthful baseline while producing different posterior probabilities.

Truthful-only enrollment can therefore support only:

- distance from the person’s known truthful baseline;
- an unusualness percentile;
- a behavioral-deviation score.

It cannot identify P(truthful | video). Adding audio increases the number and quality of measurements but does not solve this identifiability problem.

### 3.7 Probabilities require separate calibration

A classifier output or neural softmax is not automatically a probability. Modern neural networks can be poorly calibrated even when test data resemble training data, and calibration usually deteriorates under distribution shift. [Guo et al.](https://proceedings.mlr.press/v70/guo17a.html), [Ovadia et al.](https://proceedings.neurips.cc/paper_files/paper/2019/hash/8558cb408c1d76621371888657d2eb1d-Abstract.html)

The application may show a percentage only after a separate calibrator has been fitted on independent multi-session data and a prospective series of many independent sessions/outcomes demonstrates reliability across the relevant score range. A single locked T+1 session can test workflow and prospective discrimination, but it cannot establish that a 70% prediction corresponds to an event rate near 70%.

Probabilities also depend on prevalence. A calibrator fitted to a deliberately balanced truth/deception experiment does not automatically transfer to natural or public-video conditions where the class prior is unknown or different. Deployment percentages require target-population calibration or a justified prior-probability adjustment. AUPRC must always be interpreted against the target prevalence baseline.

## 4. Proposed product definition

### 4.1 Intended use

The initial application is a local, consent-based research workbench for:

- adding, editing, archiving, importing, exporting, and querying person-specific profiles;
- managing subjects and recording sessions;
- ingesting synchronized video and audio;
- creating independently supported whole-video or segment intent labels and separate claim-level factual labels;
- extracting reproducible multimodal features;
- training and validating a separate model for each subject;
- running a locked prospective T+1 study;
- analyzing factual claims against cited evidence;
- determining whether the original hypothesis survives testing.

The application does not supply or search for public footage. Initially, a user may import media only by explicitly selecting one or more local MP4 files or supplying an authorized direct HTTP(S) MP4 media URL. A folder picker may enumerate supported files for review, but the application does not recursively or silently import a directory. The user must affirm that they have the necessary rights and consent for acquisition, behavioral analysis, retention, and labeling. Public availability alone is not permission, and public footage must not silently become person-specific behavioral training data.

Webpage, playlist, streaming-service, and social-media page URLs are outside the approved input contract. The project will not bundle a general-purpose site ripper, browser-cookie extractor, DRM bypass, or background media harvester.

### 4.2 Explicitly prohibited uses

The system must not be used to make or support consequential judgments in:

- employment;
- policing or criminal investigations;
- courts or evidence evaluation;
- immigration;
- education;
- housing;
- credit, insurance, or benefits;
- healthcare;
- coercive personal or relationship disputes.

It must not automatically call anyone a liar. An experimental score is not proof of deception.

### 4.3 Recommended outputs

When training labels, aligned features, and validation support that granularity, results should be answer- or claim-level rather than one score for an entire video, because a video can mix facts, mistakes, opinions, omissions, exaggerations, and intentional deception. Otherwise, the application must stay at the coarser validated level.

For each claim or answer, the UI should show:

- **Model applicability:** Does this footage resemble validated conditions?
- **Media quality:** Are the correct face and voice measurable?
- **Behavioral deviation:** How unusual is this segment relative to the subject’s baselines?
- **Experimental deception-model score:** Only if both-class training and validation requirements are satisfied.
- **Uncertainty:** Confidence interval or uncertainty band.
- **Claim evidence:** Supported, contradicted, disputed, unresolved, or non-factual.
- **Reasons for abstention:** Missing face, poor audio, unfamiliar context, insufficient data, model instability, or failed calibration.

The default outcome is **cannot determine**, not forced binary classification.

The Query UI may present transcript sentences with clickable timestamps, but a presentation row is not automatically an independent claim or prediction unit. Result granularity must match the model's labeled and validated target. A whole-video model cannot manufacture sentence-level probabilities; sentence rows may only show a clearly shared parent result. An uncalibrated score is never shown with a percent sign. A calibrated percentage, if ever permitted, is labeled as an estimated probability of intentional deception under the named profile/protocol/context—not truth confidence or factual truth.

### 4.4 Open-source and private-data boundary

The application source, training and evaluation pipeline, schemas, tests, and reproducibility documentation are intended to be open source. Apache License 2.0 is the provisional license recommendation because it is permissive and contains an explicit contributor patent grant; the final project license must be selected before accepting outside contributions or publishing a release.

Open sourcing the software does not open the user's data. The following remain private. Original media, media derivatives, raw transcripts, free-text claim/evidence content, local paths, and source URLs always remain outside `.vwpkg`; only the strictly allowlisted model and numeric artifacts defined in Sections 8.6–8.7 may leave the workspace when the user deliberately creates and transfers an encrypted package to an explicitly authorized recipient:

- original and downloaded videos;
- extracted frames, audio, transcripts, landmarks, voice measurements, and other biometric or behavioral features;
- consent and rights records;
- labels and evidence records;
- person-specific training, calibration, and test partitions;
- trained person-specific models, calibration packages, reports, and audit history.

Users may deliberately exchange encrypted model packages with authorized colleagues. This is a user-directed export, not transmission to the project maintainers or a hosted service. Export is governed by a strict allowlist and never includes original videos, playback proxies, extracted audio, frames, face crops, thumbnails, raw transcripts, temporary files, source URLs, or original local paths. Person-specific models and numeric behavioral features remain sensitive derived data even when source media is excluded.

The public repository and public application release packages must contain no real subject media or person-specific weights. Test fixtures must be synthetic or use performers with explicit, documented releases. A future generic pretrained model would require its own model license, model card, training-data provenance, and release review.

True open-source licensing permits downstream use for any field of endeavor, including commercial use. The prohibited-use list in this document is therefore an intended-use and safety policy, not an added field-of-use restriction on the open-source license. If enforceable use restrictions are later required, the distribution would be source-available rather than OSI open source and would require a deliberate licensing decision.

## 5. Dataset and labeling protocol

### 5.1 Required label dimensions

Every claim should store these dimensions separately:

- factual status: true, false, disputed, unverifiable, or non-factual;
- speaker-belief status: believed true, believed false, or unknown;
- intent status: sincere, intentional deception, ambiguous, or unknown;
- evidence and provenance supporting the label;
- annotator and review status;
- label confidence;
- recording session and context metadata.

The behavioral classifier should train only on claims with credible intent labels. Factual incorrectness alone must not be converted into an intentional-deception label.

The Add/Edit Profile UI presents two explicit training lists: **Verified truthful videos** and **Verified intentional-deception videos**. Bucket placement is a user- or adjudicator-supplied enrollment label, never model-generated ground truth. A whole MP4 may receive one of these labels only when the user has carefully curated the entire recording and can independently support that condition for every eligible target-subject answer used in training. Store the label scope, verification method, provenance, reviewer, and confidence; eligible target-subject segments may inherit the whole-video intent label. Mixed, interrupted, noncompliant, other-speaker, voice-over, B-roll, off-condition, or uncertain material must instead receive segment-level handling or remain ambiguous and excluded. Factual status remains separate from intent.

The user-entered recording date is display and sorting metadata. It is stored exactly as entered and is not a behavioral feature or classifier input. Session identity and experimental grouping—not a filesystem timestamp—control leakage-safe splitting.

### 5.2 Required data classes

At minimum:

1. Verified sincere-truth examples.
2. Verified intentional-deception examples.
3. Unknown or ambiguous examples that are retained for auditing but excluded from supervised training.

Newly added training and T+1 media must first become a validated local MP4 artifact through one of the two approved media paths. Training never reads from a remote stream, webpage, playlist, or transient URL, and it never silently re-downloads a prior input. Once imported, the content hash—not the source URL—is the stable identity used by experiments and models. Verified numeric artifacts from an imported trainable `.vwpkg` use the separate package-import contract and do not require the original media to be present.

Useful control conditions include:

- truthful and relaxed;
- truthful under stress;
- deceptive and visibly stressed;
- calm or rehearsed deception;
- easy and difficult truthful recall;
- easy and difficult deception;
- spontaneous and prepared answers.

These controls help distinguish deception from stress, cognitive difficulty, uncertainty, and rehearsal.

For controlled collection, truth-versus-deception instructions should be randomized and counterbalanced within each subject across prompts, order, and sessions. Interviewer behavior should be standardized and, where feasible, blinded to the assigned condition. Manipulation/compliance checks should confirm that the participant understood and followed the instruction. Independent adjudicators should review label provenance, with agreement and disagreements recorded.

### 5.3 Situational matching

Training on TV interviews and evaluating on future comparable TV interviews is more defensible than transferring the model to podcasts, courtrooms, home videos, or unrelated settings. Situational matching reduces confounding but does not eliminate it.

TV interviews can still differ in:

- topic sensitivity and personal stakes;
- interviewer style and aggressiveness;
- question difficulty;
- live versus prerecorded format;
- preparation and rehearsal;
- audience presence;
- fatigue, health, mood, or medication;
- camera, microphone, editing, compression, and broadcast chain.

Truth and deception examples must not be separated by episode or production source. If all truthful labels come from one interview and all deceptive labels from another, the model may learn the host, microphone, camera, topic, or episode rather than deception.

### 5.4 Independent experimental units

Frames and overlapping windows from the same video are not independent examples. The effective sample size is based on independent sessions, prompts, and claim episodes.

Data splitting must occur by complete:

- recording session;
- synchronized capture group;
- interview or episode;
- prompt or topic;
- source and production condition.

No adjacent segment from a training interview may appear in validation or testing. All normalization statistics must be fitted using training sessions only.

Synchronized camera views of the same answer, repeated encodings of the same file, and clips cut from the same recording remain one experimental unit. They must stay together in the same split and must not inflate the effective sample size.

Whole-video labels do not make their frames, windows, transcript sentences, or answers independent. Training must weight and evaluate by session or capture event so that a longer recording cannot dominate merely because it produces more rows. Truthful and intentional-deception conditions should be paired or counterbalanced across prompt, session, source, device, topic, and camera configuration where feasible; careful curation alone does not remove source or context confounding.

### 5.5 Optional synchronized multi-angle collection

Multiple simultaneous views can improve landmark visibility, pose robustness, occlusion recovery, and quality gating. A controlled collection may use one near-frontal primary camera, one secondary camera approximately 30–45 degrees off axis, and one consistent canonical microphone. The application should associate those files as views of one session and align them to a shared timeline.

Multi-angle footage does not create additional independent truth or intentional-deception examples. All views share the same label group, and the feature pipeline either chooses the best-quality view at each timestamp or performs quality-aware fusion. Camera placement, resolution, and audio configuration must be the same across experimental conditions so that equipment does not become a label shortcut.

Separate retakes made only to obtain different angles introduce rehearsal, fatigue, order, and memory effects; simultaneous capture is preferred. Because ordinary future inputs may contain only one camera, the core query path must also work with a single MP4 and must be tested with secondary views missing. Multi-angle capture is an optional controlled-research enhancement rather than an initial query requirement. [FACSCaps](https://openaccess.thecvf.com/content_cvpr_2018_workshops/papers/w41/Ertugrul_FACSCaps_Pose-Independent_Facial_CVPR_2018_paper.pdf), [3D-aware facial landmarks](https://openaccess.thecvf.com/content/CVPR2023/papers/Zeng_3D-Aware_Facial_Landmark_Detection_via_Multi-View_Consistent_Training_on_Synthetic_CVPR_2023_paper.pdf)

### 5.6 Prospective T+1 testing

Before examining T+1 labels:

1. Freeze the outcome definition.
2. Freeze feature extraction and preprocessing.
3. Freeze train/calibration/test grouping.
4. Freeze model-selection rules.
5. Freeze quality, uncertainty, and abstention thresholds.
6. Register success and failure criteria.

The first T+1 session remains untouched until the model and protocol are locked. It provides a prospective workflow and discrimination test; random clip-level cross-validation is not sufficient evidence. Probability calibration requires a larger prospective series of independent future sessions and outcomes, not one video.

### 5.7 Ground-truth limitations in public footage

Public interviews are useful for media-pipeline development and factual claim analysis, but they rarely provide reliable labels for speaker belief or intentional deception.

- A court verdict does not establish that every statement in a clip was knowingly false.
- A later factual correction establishes possible inaccuracy, not necessarily deceptive intent.
- Confession or documentary evidence may support an intent label, but provenance and ambiguity must be recorded.
- The model must never generate its own training labels from facial or vocal behavior; doing so would make the reasoning circular.

Controlled, consented experiments in which the participant privately observes known information and is instructed to answer truthfully or deceptively provide cleaner intent labels, although instructed lies do not perfectly reproduce natural high-stakes deception.

### 5.8 Sample-size policy

No arbitrary number of frames or clips should be advertised as sufficient. A pilot should estimate between-session variability and predictive effect size, followed by a power or precision analysis based on independent sessions.

Hundreds of thousands of frames from one interview do not replace multiple genuinely independent sessions.

No finite number of truthful-only videos can identify a probability of truth. Both verified sincere-truth and verified intentional-deception examples are required for a deception classifier, and a separate prospective series is required for calibration.

For budgeting only—not as a sufficiency claim—the initial controlled pilot should anticipate roughly 20–30 independent sessions per subject, with balanced randomized conditions within sessions where the protocol permits. A credible signal evaluation may require 40–60 or more independent sessions per subject, and the final count must be recalculated from pilot variability, class balance, clustering, target precision, and expected effect size.

Five to ten recordings may be enough for engineering QA of ingest, tracking, transcription, cancellation, and the UI, but they do not support a scientific performance claim.

Generic binary-model validation literature often uses at least 100 independently supported events and 100 non-events as a starting benchmark, and approximately 200 of each for flexible calibration curves. Those figures do not transfer mechanically to this project: claims inside one recording are clustered, context matching narrows eligibility, and person-specific effects may be weak. They are a warning that a percentage-bearing result will require many independent prospective outcomes, not a promise that any fixed video count is adequate. [Riley et al.](https://onlinelibrary.wiley.com/doi/10.1002/sim.9025)

## 6. End-to-end processing pipeline

```mermaid
flowchart TB
    subgraph Ingest["Approved user-initiated ingest"]
        L["Explicitly selected local MP4"] --> I["Validated immutable local MP4 artifact"]
        U["User-supplied direct HTTP(S) MP4 URL"] --> D["Resumable download to local .part file"]
        D --> I
    end

    subgraph Training["A. Enrollment, labeling, and training"]
        T1["Verified truthful, intentional-deception, and control sessions"] --> T2["FFmpeg decode and synchronization"]
        T2 --> T3["Vision, audio, and transcript feature extraction"]
        T3 --> T4["Human-reviewed whole-video or segment intent labels and context metadata"]
        T4 --> T5["Grouped training, ablation, and validation"]
        T5 --> T6["Independent calibration and OOD thresholds"]
        T6 --> M["Versioned profile model and model card"]
    end

    subgraph Analysis["B. Analyze a future T+1 video"]
        A1["New video"] --> A2["Media and context quality checks"]
        A2 --> A3["Select and track subject"]
        A3 --> V["Vision AI"]
        A3 --> AU["Audio and speaker AI"]
        A3 --> ST["Timestamped speech-to-text"]
        V --> F["Time-aligned claim feature timeline"]
        AU --> F
        ST --> F
        F --> P["Subject-specific inference"]
        M --> P
        P --> G["Calibration, uncertainty, and OOD gate"]
        G -->|Applicable| R["Validated target-granularity experimental results"]
        G -->|Not applicable| N["Cannot determine"]
        ST --> E["Separate claim and evidence analysis"]
        E --> R
    end

    I --> T1
    I --> A1
    QP["Imported query-only .vwpkg"] --> P
    TP["Imported trainable .vwpkg"] --> T5
```

## 7. Where AI enters the application

The application is not one large language model watching an MP4. It is an orchestrated pipeline containing several models with distinct responsibilities.

### 7.1 Subject association

The user selects the subject’s face. Models then:

- detect and track the selected face across cuts;
- determine whether the subject is visible;
- identify speaker turns;
- associate the subject’s voice with the visible face;
- exclude the host, other guests, voiceovers, and B-roll;
- emit confidence and missingness values.

Segments with uncertain identity or speaker association must be excluded or flagged.

### 7.2 Visual feature extraction

Candidate measurements include:

- facial landmarks and their dynamics;
- facial action units;
- head pose and movement;
- gaze direction;
- blink timing;
- face-detection and tracking confidence;
- proportion of valid frames;
- lighting, occlusion, resolution, and cut indicators.

The first system should avoid treating opaque emotion labels as ground truth. It should preserve observable measurements and their quality.

When synchronized views exist, each feature row records the shared capture-group ID, view role, synchronization confidence, and per-view quality. All views and derivatives remain grouped. The production model must either use a canonical primary view with secondary views limited to tracking/quality assistance, or explicitly validate missing-view behavior and a single-view ablation before multi-view fusion can be used for ordinary one-video queries.

Visual extraction must sit behind a versioned adapter so that a tracker or model can be replaced without changing the experiment contract. OpenSeeFace is the provisional $0 candidate because its repository explicitly distributes its code and models under BSD-2-Clause; its model and training-data provenance must still be recorded in the dependency register. MediaPipe is a newer candidate, but its downloadable model-bundle rights and telemetry behavior require a separate audit before it can be bundled or approved for a public release. [OpenSeeFace](https://github.com/emilianavt/OpenSeeFace), [MediaPipe](https://github.com/google-ai-edge/mediapipe)

OpenFace remains useful as a research comparison, but its noncommercial research license prevents it from becoming a required dependency of the distributable open-source application. Its implementation must not be copied into the project. [OpenFace license](https://github.com/TadasBaltrusaitis/OpenFace/blob/master/OpenFace-license.txt)

### 7.3 Audio feature extraction

Candidate measurements include:

- fundamental frequency relative to the subject’s range;
- intensity and energy;
- voice-quality and spectral measures;
- pause duration;
- response latency;
- speaking rate;
- hesitation and interruption timing;
- clipping, signal-to-noise, and missing-audio indicators;
- speaker and voice-activity confidence.

The distributable baseline should use permissively licensed, locally executed components: librosa/SciPy for transparent acoustic measurements and Silero VAD through ONNX for speech-region detection. Component and model licenses must be recorded separately. [librosa license](https://github.com/librosa/librosa/blob/main/LICENSE.md), [Silero VAD](https://github.com/snakers4/silero-vad)

OpenSMILE/eGeMAPS remains a useful research comparison, but openSMILE's research/noncommercial terms prevent it from becoming a required dependency of a generally reusable open-source release without separate permission. Its measurements may inform independent feature-design experiments; its code is not copied or bundled. [openSMILE license](https://github.com/audeering/opensmile/blob/master/LICENSE)

### 7.4 Speech recognition and text features

Local speech recognition produces word timestamps and a draft transcript. The initial candidate is local Whisper inference through whisper.cpp or another audited MIT-compatible runtime; no hosted transcription API is required. Whisper's source and model weights are MIT-licensed. The original ASR output and any human-corrected version must be preserved separately inside the authorized workspace and are never included in a `.vwpkg` export. [Whisper](https://github.com/openai/whisper), [whisper.cpp](https://github.com/ggml-org/whisper.cpp)

Candidate text/timing measurements include:

- answer length;
- hesitations and self-corrections;
- temporal and sensory detail;
- response latency;
- pronoun and distancing patterns;
- internal contradictions;
- consistency with earlier statements.

Topic and source leakage are major risks. Learned text embeddings or large-language-model features should initially be evaluated only as separate ablations, not silently mixed into the core score.

### 7.5 Personalized classifier

The first model should use pretrained systems for perception and a comparatively simple per-subject classifier:

- regularized logistic regression;
- a small gradient-boosted model;
- simple late fusion of face, audio, and text branches.

This is preferable to an end-to-end deep deception model when the number of independent sessions is small. A larger model can memorize topic, production artifacts, or sessions.

The application must compare:

- constant class-prior baseline;
- metadata-only baseline;
- face only;
- audio only;
- transcript only;
- audiovisual;
- complete multimodal fusion.

If metadata alone predicts the label, the dataset is confounded.

An imported query-only ONNX model is frozen inference data, not a resumable training checkpoint. An imported trainable package may support a new candidate model only by combining its compatible finalized feature rows with features from new curated media and rerunning the complete grouped training and validation protocol.

### 7.6 Calibration and out-of-distribution detection

Quality rejection happens before learned inference. The system then assesses whether the new feature distribution resembles the model’s validated training conditions.

Out-of-distribution means **unlike the validated data**. It does not mean deceptive.

For small datasets, a low-parameter sigmoid/Platt or temperature calibrator is more realistic than a highly flexible calibrator. It must be fitted using predictions from independent calibration sessions, never the classifier’s fitting data.

### 7.7 Optional evidence and language-model module

A separate module may:

- split the transcript into claims;
- classify claims as factual, opinion, prediction, or unverifiable;
- search trusted sources;
- compare claims with earlier statements;
- report supported, contradicted, disputed, or unresolved;
- attach citations and evidence provenance.

This module evaluates claim support, not demeanor or speaker intent. Its result should remain visually and statistically separate from the behavioral classifier.

## 8. Windows/C# application architecture

### 8.1 Recommended implementation strategy

- **Desktop UI:** .NET 10 LTS, C#, and WinUI 3. Microsoft identifies WinUI 3 as its recommended modern native framework for new Windows desktop applications. WPF remains a reasonable fallback if mature media-control compatibility materially accelerates the research prototype. [WinUI 3](https://learn.microsoft.com/en-us/windows/apps/winui/winui3/), [.NET 10](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-10/overview)
- **Media:** A version-pinned FFmpeg/ffprobe distribution for reproducible probing, decoding, audio extraction, proxy creation, and timestamp mapping. Bitwise identity is not assumed across builds, hardware acceleration, or platforms. Use a pinned CPU/reference path for accepted artifacts where practical, and record the build, hardware path, configuration, and output hashes. [FFmpeg documentation](https://www.ffmpeg.org/documentation.html), [ffprobe documentation](https://ffmpeg.org/ffprobe.html)
- **HTTP acquisition:** .NET `HttpClient` with validated byte-range resume, strong resource validators, redirect and size limits, staged `.part` files, and explicit user control. [RFC 9110 range requests](https://www.rfc-editor.org/rfc/rfc9110.html), [.NET RangeHeaderValue](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.headers.rangeheadervalue?view=net-10.0)
- **Metadata:** SQLite through Microsoft.Data.Sqlite.
- **Large feature tables:** Parquet/Arrow rather than putting frame- and word-level arrays into SQLite.
- **Research training:** A pinned local Python environment using scikit-learn/PyTorch and data tooling.
- **C# inference:** Export frozen preprocessing and models to ONNX and execute them locally with Microsoft.ML.OnnxRuntime. ONNX Runtime provides a supported C# API and CPU/GPU execution options. [ONNX Runtime C#](https://onnxruntime.ai/docs/get-started/with-csharp.html)
- **Simple .NET models:** ML.NET is an option for basic classifiers and ONNX integration. [ML.NET](https://learn.microsoft.com/en-us/dotnet/machine-learning/mldotnet-api)
- **Integration:** During research, C# launches versioned local worker processes using JSON job specifications and JSON-lines progress/results. The user never interacts with Python directly.
- **Deployment direction:** After feature extraction and models stabilize, move frozen inference into C#/ONNX while retaining the Python worker for controlled training.
- **Portable packages:** Import and export versioned, encrypted, ZIP-based `.vwpkg` archives containing only allowlisted model or numeric training artifacts. Package parsing never executes scripts or plugin code.
- **Dependency selection order:** scientific suitability; explicit permission for $0 local use and redistribution; active maintenance and modern architecture; replaceability and testability; then convenience or raw benchmark performance.
- **Network boundary:** no mandatory hosted AI, media-processing API, or account. A media URL is used only for an explicit user-initiated download and is not an inference endpoint.

### 8.2 Suggested solution structure

```text
VerityWorkbench.sln
  VerityWorkbench.App
    WinUI views, view models, playback, labeling, and results
  VerityWorkbench.Core
    Domain models, use cases, validation rules, and interfaces
  VerityWorkbench.Media
    FFmpeg orchestration, probing, proxies, and timestamp mapping
  VerityWorkbench.Jobs
    Worker lifecycle, progress, cancellation, caching, and recovery
  VerityWorkbench.Inference
    ONNX Runtime, feature contracts, calibration, OOD, and abstention
  VerityWorkbench.Data
    SQLite repositories, Parquet artifacts, manifests, and audit log
  VerityWorkbench.Packaging
    Validated model-package import, export, encryption, and integrity checks
  VerityWorkbench.Tests
    Unit, integration, parity, leakage, and reproducibility tests
  workers/
    Python extraction, training, evaluation, calibration, and export
```

### 8.3 Deterministic versus learned responsibilities

| Deterministic or human-controlled | Learned or fitted |
|---|---|
| File hashing and provenance | Face detection and tracking |
| ffprobe validation and timestamp mapping | Facial landmarks/action units |
| Transcoding configuration | Voice activity and speaker models |
| Human label entry and evidence | Speech recognition |
| Feature schema and missingness rules | Optional speech/text embeddings |
| Enforcement of grouped data splits | Subject-specific classifier |
| Metric and audit calculations | Modality fusion |
| Frozen quality and abstention rules | Probability calibrator |
| Model/package version checks | Statistical OOD model |
| Package allowlist, integrity, lineage, and compatibility checks | None—packages never bypass deterministic validation |
| Atomic model-version promotion and archive enforcement | None—learned components cannot promote themselves |

The deterministic layer owns safety, provenance, reproducibility, and refusal behavior. Learned components never bypass those gates.

### 8.4 Media ingest

The initial ingest service accepts MP4 and exposes exactly two user-initiated entry paths:

1. **Local-file import:** the user explicitly selects one or more local MP4 files. A folder picker may list supported files, but the user chooses the files to add; there is no recursive or background import. The application copies each selection into the profile's private workspace and never modifies the user's source file.
2. **Direct-URL import:** the user supplies a direct HTTP(S) MP4 media URL. The application downloads the response into the profile's selected download-staging folder as a randomly named `.part` file before any media processing begins.

Each profile has a user-selected **workspace root** for its complete workload and a user-selected **download-staging root**, which defaults to a clearly named `Downloads` folder inside the workspace. The application validates both roots, creates only well-named profile and job subfolders beneath them, and never treats a drive root or other broad folder as a deletion target.

The direct-URL path is deliberately narrow:

- accept only HTTP and HTTPS and reject redirects to other schemes;
- require an explicit user action and rights/consent attestation for every acquisition;
- apply configurable byte, duration, redirect, and timeout limits;
- use the full URL only in memory for the active request, and strip credentials, query strings, and fragments from logs and ordinary persistent metadata;
- validate the received content as MP4 rather than trusting its extension or declared MIME type;
- reject HTML pages, playlists, manifests intended for segmented streaming, other containers, and corrupted media;
- do not use browser cookies, account sessions, DRM workarounds, page extractors, social-site adapters, or a general-purpose site ripper;
- never automatically re-fetch a URL during training, evaluation, analysis, or project reopen, except for an explicit resume of that pending download;
- atomically finalize the download only after validation succeeds.

Large downloads should be resumable when the server and resource permit it:

1. Store the completed byte count, expected total length, sanitized source reference, ETag or strong Last-Modified validator, and download state in a resume manifest beside the `.part` file.
2. On explicit Resume, request the remaining byte range and use `If-Range` so bytes are appended only when the remote representation is unchanged.
3. Require a valid `206 Partial Content` response and matching `Content-Range` before appending. If the server ignores the range, lacks a usable validator, or the resource changed, do not combine bytes; restart safely or ask the user what to do.
4. A signed or credential-bearing URL is not persisted in plaintext. If it expires or is unavailable after restart, the user supplies a fresh direct URL and the application verifies that it identifies the same resource before resuming.
5. Pausing or cancelling acquisition closes the response, stream, and file handle, retains the `.part` file and resume manifest, and exposes **Resume** and **Discard Download**. Discard is the explicit action that deletes resumable state.

After either entry path produces a finalized local artifact:

1. Compute SHA-256 and use the hash as the durable media identity.
2. Preserve the imported original without modification while the project remains authorized; content-addressed storage must still support consent withdrawal and verified deletion.
3. Record the source type (`LocalFile` or `DirectHttpMedia`), a sanitized source reference, the user's rights/consent attestation, user-entered recording-date label, archive state, ffprobe stream metadata, and tool version.
4. Detect variable frame rate, dropped frames, clipping, missing audio, and timestamp discontinuities.
5. Create a canonical playback proxy if necessary.
6. Create a mono analysis WAV while preserving a timestamp map to the source.
7. Cache artifacts by source hash plus pipeline-configuration hash.
8. Record the decode path and hash generated proxy/audio outputs so reproducibility differences are visible.

Both model training and T+1 analysis consume only these finalized, validated local artifacts. Remote content is never streamed directly into a feature extractor or model.

### 8.5 Storage layout

The user chooses the profile workspace when adding the profile. The download-staging root is independently selectable and defaults to `Downloads` inside that workspace. Fixed, human-readable top-level folder names make the workload inspectable; leaf names combine a safe display label or job kind with a short stable identifier, while full hashes remain in manifests and the database.

```text
<user-selected-profile-workspace>/
  Profile/
    profile.sqlite
    profile-manifest.json
  Media/
    <recording-label>_<safe-source-name>_<short-id>/
      original.mp4
      proxy.mp4
      audio.wav
      media-manifest.json
  Downloads/
    <download-date>_<safe-name>_<short-id>/
      media.part
      resume.json
  Processing/
    <utc-time>_<job-kind>_<short-id>/
      job.json
      status.json
      Logs/
      Intermediate/
      Output/
  Features/
    <pipeline-hash>/<media-sha>/*.parquet
  Models/
    <model-version>/
      model.onnx
      preprocessing.json
      feature-schema.json
      calibration.json
      ood.json
      quality-thresholds.json
      manifest.json
      model-card.md
  Exports/
  Reports/
```

If the user chooses a download-staging root outside the workspace, the application creates the same well-named profile/download job boundary there. Names are for human navigation only and never become model features or durable identities.

Every processing run is confined to one unique `Processing` job folder. Cancellation requests cooperative worker shutdown, terminates an unresponsive worker tree after a bounded grace period, disposes streams and child processes, closes every file handle, records `Cancelled`, and leaves the job folder intact and unlocked for later inspection or deletion. No incomplete feature set or candidate model is promoted. The UI provides **Open Folder** and **Delete Processing Data**, and deletion validates that the exact target is an inactive job folder beneath the selected profile workspace.

Archiving a training video is a logical metadata action: the artifact and audit history remain in place, but the video is excluded from future candidate models. Permanent deletion and consent withdrawal are separate operations. Withdrawal disables any active model derived from the withdrawn data and triggers the configured deletion workflow.

Each artifact manifest records:

- source and upstream hashes;
- tool and model versions;
- command/configuration hashes;
- feature schema;
- random seeds;
- environment details;
- timestamps;
- training, calibration, and test session/capture-group IDs;
- profile lineage, parent model version, compatibility contract, and promotion state where applicable.

Python-versus-C# inference parity must be tested before accepting an ONNX export. A destination import also verifies feature schema, preprocessing hash, ONNX opset/runtime support, and required worker/model versions before allowing queries.

### 8.6 Portable profile packages and colleague exchange

Add Profile and Edit Profile accept a VerityWorkbench package (`.vwpkg`), curated training MP4s, or both. The application imports only a complete package, never a loose ONNX file, because valid inference also requires the exact preprocessing, feature schema, calibration, OOD, quality, provenance, and compatibility contracts.

Two private export types are supported:

1. **Query-only package:** the frozen ONNX model, preprocessing contract, feature schema, calibration, OOD and quality thresholds, model card, compatibility manifest, and checksums. It enables local query-video processing without the original training media but cannot extend the original training history.
2. **Trainable profile package:** everything in the query-only package plus the minimum finalized numeric model-input features, coded intent labels, independent-session and capture grouping metadata, historical split/audit metadata, training configuration, and sanitized provenance required to reproduce training. It never contains original media.

The input combinations behave as follows:

| Imported package | New training videos | Behavior |
|---|---|---|
| None | Yes | Build a new profile and candidate model. |
| Query-only | None | Become query-ready after import checks. |
| Query-only | Yes | Keep the imported model query-ready; the new videos cannot extend its unavailable historical data and form a separate candidate dataset. |
| Trainable | None | Become query-ready and remain extendable later. |
| Trainable | Yes | Retrain a new candidate from the combined compatible old and new features. |
| None | None | Reject profile creation because it has no usable input. |

Continued training means building a new immutable model version from combined compatible feature data, not merely adjusting imported weights. The application reruns grouped model selection, validation, calibration, OOD fitting, and eligibility checks. Previously exposed test results remain audit evidence but are not treated as a new locked prospective test. An unsupported feature schema must be processed with an installed compatible legacy pipeline or rejected; because media is never exported, old features cannot be regenerated under a newer schema.

The active validated model remains available during ordinary importing, extraction, and retraining. A candidate is promoted atomically only after integrity, compatibility, Python/C# parity, scientific-validation, and eligibility checks pass. Cancellation or failure never replaces the active model. This continuity does not apply after consent withdrawal or required deletion.

ONNX and C# runtime portability allow the same validated calculation to run on a compatible second machine; they do not validate a new person, camera, context, population, or protocol. The destination still processes every query MP4 locally and applies quality and OOD gates.

### 8.7 One-file `.vwpkg` format and export rules

Every export produces exactly one file named in the form `ProfileName_ModelVersion.vwpkg`. The package is a compressed ZIP payload wrapped in authenticated portable encryption; it does not use legacy ZipCrypto, and machine-bound DPAPI is not the portable encryption layer. After import, approved contents are re-encrypted using the destination workspace's local at-rest protection.

Export uses a strict allowlist rather than copying a directory and deleting unwanted files afterward. The final sequence is:

1. Stage only allowlisted artifacts inside a private export-job folder.
2. Generate the manifest and per-entry cryptographic digests.
3. Reject prohibited extensions, unexpected entries, unsafe names, and external references.
4. Compress the approved payload as ZIP and wrap it in authenticated portable encryption.
5. Decrypt, reopen, validate, and re-hash the staged result.
6. Calculate the exact encrypted file size and final package checksum.
7. Show the review, then save exactly one `.vwpkg` file.

The review shows package type, profile alias, model version, represented independent-session counts, exact final byte size, included artifact categories, encryption and integrity state, and the statement **Original media and media derivatives are not included**.

The following are prohibited:

- original or downloaded MP4 files;
- playback proxies, extracted audio, frames, face crops, thumbnails, or other media fragments;
- partial downloads, processing intermediates, caches, and temporary files;
- original ASR or corrected transcript text and free-text claim/evidence content;
- local paths, URLs, credentials, query parameters, or fragments;
- scripts, executables, native libraries, plugins, symlinks, nested archives, or ONNX external-data references.

There is no export override for any prohibited category: source media and all listed media/content derivatives are never exportable. Trainable packages contain only models, finalized numeric features actually consumed by the model, coded labels linked to opaque IDs, grouping metadata, configurations, and sanitized provenance. Numeric features and embeddings can still reveal sensitive behavioral or content information, so export requires a derived-data sharing attestation and encryption.

Treat every imported `.vwpkg` as untrusted. Before promotion, verify authenticated encryption, manifest schema, entry digests, package type/version, pseudonymous profile lineage, feature/preprocessing hashes, ONNX opset/runtime compatibility, allowed operators, declared size/count limits, and the absence of traversal paths or prohibited content. Extraction occurs only inside a quota-limited private import-job folder and never fetches dependencies or follows embedded links.

Expected package sizes are planning estimates until the feature schema is frozen: a query-only package will often be approximately 1–20 MB; a trainable package may require roughly 1–10 MB per 30-second single-view training video. The export review always reports the measured final size rather than relying on these estimates.

### 8.8 Processing-time expectations

Initial planning estimates for one local 30-second, 1080p, single-face MP4, excluding download time, are:

| Hardware | Estimated local processing time |
|---|---:|
| Recent machine with a supported GPU | Approximately 10–45 seconds |
| Recent 6–12-core CPU without acceleration | Approximately 30 seconds–2 minutes |
| Older or low-power computer | Approximately 2–5 minutes |

These are design estimates, not performance claims. Resolution, frame rate, face count, proxy generation, ASR model size, synchronized extra views, thermal limits, and worker configuration can increase the time. OpenSeeFace reports real-time-or-faster single-face tracking on a suitable CPU, while voice-activity detection is expected to be a small fraction of total time; transcription and visual extraction will usually dominate. [OpenSeeFace](https://github.com/emilianavt/OpenSeeFace), [Silero VAD](https://github.com/snakers4/silero-vad)

Scoring cached features should normally take less than a second. Retraining can take seconds to minutes because grouped selection and validation rerun. The processing screen records stage-level timings and replaces generic estimates with a learned local ETA after enough completed jobs. The initial CPU-only engineering target is under two minutes for the stated 30-second reference input and must be verified on declared reference hardware.

### 8.9 Open-source distribution and dependency governance

The implementation must be original and auditable. External source code must not be copied, translated, structurally mirrored, or redistributed unless an explicit compatible license or written permission permits it. Research concepts that influence the implementation must be cited appropriately.

Related research context: the [MMDD 2025](https://codalab.lisn.upsaclay.fr/competitions/22162) and [MMDD 2026](https://www.codabench.org/competitions/12678/) Multimodal Deception Detection competitions took place as part of the wider research field.

The public repository should contain:

- the application's original source code, build files, training and evaluation code, tests, schemas, and documentation;
- a root `LICENSE` and `NOTICE` appropriate to the selected project license;
- `THIRD_PARTY_NOTICES.md` with code, binary, model, and asset licenses tracked separately;
- an SPDX software bill of materials for every release;
- a model registry recording each weight file's origin, license, checksum, training-data information, and redistribution status;
- lock files, exact tool versions, build configurations, and reproducible release hashes;
- a contribution policy using signed-off provenance or an equivalent contributor-rights process;
- synthetic fixtures or performer media with explicit releases—never real user training data.

Here, **release package** means a public VerityWorkbench application/source release. A private, user-generated `.vwpkg` is not a public software release and may contain person-specific weights or allowlisted numeric features under the explicit export policy; it still never contains media or media derivatives.

Dependencies with no license, research-only/noncommercial terms, ambiguous model-weight rights, mandatory cloud processing, or incompatible copyleft obligations are not approved for the core distributable build. LGPL FFmpeg can remain a separate version-pinned component only with its exact build configuration, license notices, and corresponding-source obligations satisfied; GPL and nonfree FFmpeg variants are excluded from the default distribution. [FFmpeg license](https://github.com/FFmpeg/FFmpeg/blob/master/LICENSE.md)

## 9. Core domain entities

The initial database should model:

- **Project:** purpose, owner, protocol version, consent policy, and retention policy.
- **Profile:** stable pseudonymous lineage ID, display name, user-selected workspace and download roots, readiness, active model version, pending changes, consent status, withdrawal status, and model eligibility.
- **Subject:** pseudonymous ID and the consent/protocol records associated with the profile; no real-world identity is required by the software.
- **Media asset:** original hash, approved source type, sanitized source reference, rights/consent attestation and provenance, user-entered recording-date label, verified training class, active/archived state, local storage state, stream metadata, and quality.
- **Download:** sanitized source metadata, `.part` and resume-manifest paths, received and expected byte counts, strong validator, resumability state, and user action history.
- **Session or capture group:** stable group ID, associated synchronized camera views, format, interviewer, topic, stakes, rehearsal, device, environment, production source, synchronization metadata, and camera-angle roles. The display-date label is not proof of independence.
- **Processing job:** unique inspectable folder, job kind, state, stage timings, progress, cancellation reason, worker identities, and produced-artifact references.
- **Segment:** start/end timestamps, speaker, question, answer, claim, and quality.
- **Label:** label scope, factual status, belief status, intent status, verification method, evidence/provenance, annotator/reviewer, and confidence.
- **Feature artifact:** pipeline version, schema, hashes, missingness, and storage path.
- **Experiment:** frozen outcome, feature set, groups, splits, seeds, metrics, and protocol.
- **Model version:** immutable profile lineage, parent/import lineage, training data, classifier, calibrator, OOD and quality rules, compatibility contract, validation result, active/candidate/archived state, and model card.
- **Model package:** query-only or trainable type, package version, represented model version, exact size, checksum, encryption/integrity state, compatibility metadata, and import/export audit data.
- **Analysis:** model version, input hash, per-segment results, applicability, uncertainty, and abstention.
- **Audit event:** append-only record of imports, label edits, archive/unarchive, download pause/resume/discard, processing cancellation, training, model promotion, analysis, package import/export, withdrawal, and deletion.

## 10. Application screens and workflow

### 10.1 Main view

The primary navigation presents three actions:

1. **Add Profile**
2. **Edit Profile**
3. **Query Profile**

Profile cards show the pseudonymous display name, active model version, readiness, pending changes, and background job status. States include `Draft`, `Downloading`, `Processing`, `Needs Review`, `Training`, `Validating`, `Ready — Baseline Only`, `Ready — Experimental Model`, `Ready — Imported Query Model`, `Cancelled`, `Update Failed`, and `Cannot Model Reliably`. During an ordinary update, the last validated model remains clearly identified and queryable until a replacement passes every promotion gate.

### 10.2 Add Profile

The Add Profile flow:

1. Enter a pseudonymous profile name.
2. Choose the profile workspace root and optionally a separate download-staging root.
3. Choose an imported `.vwpkg`, curated training videos, or both.
4. Maintain separate **Verified truthful videos** and **Verified intentional-deception videos** lists.
5. Add local MP4s directly, choose a folder and explicitly select supported MP4s from its displayed list, or add direct HTTP(S) MP4 URLs one at a time.
6. Enter a recording-date label for display and sorting; add, remove, and reorder items before processing.
7. Confirm acquisition rights, subject consent, intended use, retention, and the provenance of the assigned training condition.
8. Choose **Cancel** or **Save & Process**. Cancel discards unsaved setup. Save & Process returns to the main view while work continues in the background.

After decoding begins, the user selects the subject's face, confirms speaker association, and reviews ambiguous tracking. A profile with a query-only imported package may become query-ready after compatibility checks without processing historical training videos.

### 10.3 Edit Profile

Edit Profile allows the user to:

- rename the display alias without changing the stable profile lineage;
- review or change the selected workspace/download roots through a verified relocation workflow;
- add new truthful or intentional-deception MP4s and direct-media URLs;
- remove an unprocessed selection;
- archive or unarchive existing training videos;
- reprocess selected active training videos with the current compatible pipeline;
- inspect media and processing folders;
- import a newer compatible query-only or trainable `.vwpkg`;
- export the active model;
- submit relevant changes with **Save & Process**.

Archiving retains the media and audit history but excludes the item from future candidate training. Permanent deletion and consent withdrawal remain separate actions. Saving a material change creates a new processing job and immutable candidate model version. A failed or cancelled update never replaces the active validated model.

Reprocessing reads the immutable validated local MP4 and starts a new bounded job with a new pipeline-configuration identity. It may reuse only artifacts whose source and configuration hashes prove compatibility; otherwise it creates new versioned derivatives without overwriting the prior accepted artifacts. Reprocessing cancellation or failure leaves both the source and active model unchanged.

When a package is attached through Edit Profile, its pseudonymous profile-lineage identifier must match. The application rejects an unrelated person's package rather than silently merging profiles.

### 10.4 Processing status, download resume, and cancellation

The main view and profile detail view show current stage, progress, elapsed time, learned ETA when available, input item, job folder, and any action required. Download jobs expose **Pause**, **Resume**, and **Discard Download** when safe resume is available.

Cancelling a processing job stops the full worker tree, closes every stream and file handle, records the cancellation, and leaves its unique processing folder intact and unlocked. No partial artifact or model is promoted. The UI exposes **Open Folder** and **Delete Processing Data**, allowing the user to inspect or later remove the bounded job directory.

### 10.5 Query Profile

The Query Profile flow:

1. Select a query-ready profile by name.
2. Select a local MP4 or enter a direct HTTP(S) MP4 URL and confirm the applicable rights/consent attestation.
3. For a remote input, finish or resume the download and finalize local MP4 validation before analysis starts.
4. Confirm the correct subject if multiple faces or speakers appear.
5. Run media, identity, speaker, context, feature, OOD, and model-applicability checks.
6. Open the synchronized results view.

The results view displays a video player, current playback timestamp, and time-aligned transcript sentence, answer, or claim rows. Each row has a clickable start timestamp that seeks the player. The original ASR and any corrected transcript remain separate inside the workspace.

Each eligible result row keeps the following visually separate:

- model applicability and media quality;
- behavioral deviation;
- experimental deception-model score, without a percent sign when uncalibrated;
- only when Section 11.5 passes, **Estimated probability of intentional deception under this profile, protocol, and context**, with uncertainty;
- separate factual-evidence status;
- reason for abstention or **cannot determine**.

The interface never labels an output `% truth`, `truth confidence`, or sentence truthfulness, and it never treats one minus a deception score as factual truth. Transcript sentences are presentation and seek units, not automatically independent statistical observations. Output granularity must match the model's trained and validated target: a whole-video model may show only a shared video/answer result, while true per-answer or per-claim output requires aligned labels, features, grouped evaluation, and calibration at that level. Query results are never automatically recycled as training labels.

### 10.6 Synchronized label and tracking review

- Video player with frame-accurate timeline.
- Subject-face selection, synchronized-view grouping, and tracking review.
- Waveform, speaker turns, and original/corrected transcript.
- Whole-video label scope where every eligible target-subject segment shares the verified condition; otherwise question, answer, and claim boundaries with segment labels.
- Factual, belief, intent, verification method, provenance, reviewer, and confidence fields.
- Face/audio/ASR confidence overlays.
- Mandatory human review before a training label becomes eligible.

### 10.7 Experiment designer

- Select a profile and eligible independent sessions or capture groups.
- Define outcome and inclusion criteria.
- Lock grouping variables, splits, features, random seeds, metrics, and thresholds.
- Show independent session counts rather than inflated frame, sentence, or camera-view counts.
- Warn about class/source/topic/device/camera leakage.

### 10.8 Training and validation

- Run constant-prior and metadata-only baselines.
- Train unimodal and fused models from the currently eligible grouped data.
- Display ablations, grouped performance, reliability curves, and uncertainty.
- Reject models that fail predeclared criteria.
- Generate a model card and immutable internal model version that remains deletable when authorization is withdrawn.
- Promote a candidate atomically only after validation succeeds.

### 10.9 Model import and export

The import action accepts exactly one `.vwpkg` file, displays its package type and declared lineage, validates it in a private staging job, and explains whether the resulting profile is query-only or extendable. It never runs package-provided code or downloads missing dependencies.

The export action offers **Query-only model** and **Trainable profile**. It builds and verifies the encrypted package in private staging, then shows the exact final size, represented session count, model version, included categories, checksum, and the explicit media-exclusion statement before the user selects the final destination. A successful export produces exactly one `.vwpkg` file.

## 11. Training and validation protocol

### 11.1 Model-selection strategy

Start with simple models and late fusion. Complexity must be earned by improvement on independent sessions, not training accuracy.

Required comparisons:

1. Constant class-prior baseline.
2. Metadata-only baseline.
3. Face-only model.
4. Audio-only model.
5. Transcript-only model.
6. Audiovisual model.
7. Full multimodal model.

If a simpler model performs equivalently, prefer it.

When a compatible trainable profile package is extended, fit a fresh candidate from the combined eligible old and new feature data. Do not treat warm-starting or fine-tuning imported weights as a substitute for complete regrouping, validation, calibration, OOD fitting, and eligibility review.

### 11.2 Data splitting

Use grouped nested validation:

- outer groups for honest model evaluation;
- inner groups for feature and hyperparameter selection;
- separate grouped calibration sessions;
- one final locked prospective T+1 test.

Grouping variables include explicit session or synchronized capture-group ID, episode, prompt/topic, device, interviewer, source, and production condition. Dependent observations—including simultaneous views, excerpts, retakes, frames, windows, and transcript rows—must never cross split boundaries. The user-entered recording-date label is used only for display and sorting; it is not used to infer independence or as a model, calibration, OOD, or eligibility input. If future temporal validation needs time ordering, collect a separate provenance-bearing verified collection-order field. [Grouped validation guidance](https://scikit-learn.org/stable/modules/cross_validation.html#cross-validation-iterators-for-grouped-data)

### 11.3 Required metrics

Discrimination:

- balanced accuracy;
- sensitivity and specificity;
- AUROC;
- AUPRC together with the class-prevalence baseline;
- confusion matrix.

Probability quality:

- Brier score;
- log loss;
- reliability diagram;
- expected calibration error as a secondary summary;
- calibration intercept and slope.

Uncertainty and robustness:

- session-clustered confidence intervals;
- performance by session, topic, device, environment, and relevant demographic/accessibility groups;
- missing-modality performance;
- abstention coverage and error rate;
- source-removal and artifact tests.

Overall accuracy alone is insufficient.

### 11.4 Leakage tests

Required negative controls include:

- metadata-only prediction;
- training on production artifacts with behavioral features removed;
- shuffled labels within valid grouping constraints;
- source/episode prediction from feature vectors;
- models with face, audio, text, or context individually removed;
- testing after removing backgrounds, overlays, or non-subject audio where appropriate.

The goal is to determine whether the model learned deception-related behavior or an accidental shortcut.

### 11.5 Calibration policy

A percentage is permitted only if:

1. Both classes are credibly labeled.
2. The classifier was trained without calibration/test leakage.
3. Calibration uses separate complete sessions.
4. Reliability is demonstrated across many independent prospective sessions/outcomes in the target condition and relevant score range.
5. Uncertainty is reported.
6. The context and quality gates pass.
7. Performance remains materially above the class-prior and metadata-only baselines.
8. Calibration prevalence matches the intended target population, or a justified prior adjustment is documented.

Otherwise, display **experimental model score**, **behavioral deviation**, or **cannot determine**.

An uncalibrated score is displayed without a percent sign. A permitted percentage is labeled **Estimated probability of intentional deception under this profile, protocol, and context** and includes uncertainty and applicability. Its complement is not factual truth. An imported package must carry the exact calibration population, prevalence assumptions, target granularity, protocol, and context, and the destination applies the same gates before displaying it.

### 11.6 Subject eligibility

The system must permit the conclusion that a subject is not modelable. A subject-specific model should not be promoted when:

- prospective discrimination is indistinguishable from chance;
- calibration is unstable;
- results depend on a single session or topic;
- metadata predicts labels;
- signals drift substantially across prospectively ordered collection sessions;
- error or abstention rates exceed frozen limits.

### 11.7 Model-update and promotion policy

Any change to eligible training membership creates a new candidate version and invalidates the old candidate's calibration and evaluation claims for that changed dataset. Historical models, splits, and metrics remain immutable for audit. The application may reuse compatible cached feature artifacts, but it reruns the prescribed training and validation workflow and requires new prospective evidence for new scientific claims.

The active model pointer changes only through an atomic promotion after all frozen checks pass. Update cancellation, worker failure, package incompatibility, or failed validation leaves the prior active version unchanged. Consent withdrawal or required deletion is an exception: affected models are disabled immediately rather than kept queryable.

## 12. Security, privacy, legal, and ethical constraints

Video, voice, face geometry, transcripts, and inferred behavioral traits are sensitive. The default architecture is local-first:

- no telemetry by default;
- no automatic, hosted, or maintainer-directed transmission of videos, audio, transcripts, biometric features, labels, consent records, reports, or person-specific models; the only model exchange is an explicit encrypted `.vwpkg` export initiated by the user for an authorized recipient;
- no cloud media processing or mandatory online account;
- direct media URLs are used only for an explicit user-requested download into the user's private local workspace;
- source URLs are sanitized before persistence so credentials, signed query parameters, and fragments never enter logs or reports;
- per-project or per-subject encryption keys and encrypted backups;
- BitLocker or equivalent full-disk protection;
- Windows DPAPI/Credential Manager for local secrets, with separate authenticated portable encryption for `.vwpkg` transfer because DPAPI is machine-bound;
- least-privilege access;
- append-only audit history for authorized records, with only a non-identifying deletion event retained after withdrawal;
- configurable retention, cache garbage collection, and verified deletion of originals, derivatives, features, models, and temporary artifacts after withdrawal;
- backup expiration plus cryptographic erasure by destroying the applicable project/subject key when immediate physical deletion from every backup generation is impractical;
- export controls and visible watermarks/disclaimers on reports;
- no covert analysis.

Profile workspace and download roots are user selected, canonicalized, and recorded. Every temporary, download, import, export, and processing operation is confined to a unique bounded job folder. Destructive cleanup validates that the resolved target remains under the applicable selected root and that no active worker owns it.

Private package export is allowlist-based and fails closed. It excludes media, media derivatives, raw transcripts, free-text claims/evidence, paths, URLs, credentials, executable payloads, and unapproved artifacts by construction, then validates the final encrypted archive. Package import treats the archive as hostile input, applies path and decompression quotas, validates lineage and compatibility, and never executes or fetches package-provided content.

Excluding media reduces exposure but does not make a package anonymous or harmless. Person-specific weights, numeric face/voice features, embeddings, coded labels, and provenance can remain sensitive and require consent, retention controls, encryption, and authorized sharing.

The public open-source repository, issue templates, crash reports, test fixtures, and public application release packages must not contain user media, media fragments, derived biometric features, transcripts, original local paths, consent documents, or person-specific weights. This public-release rule is distinct from a private user-generated `.vwpkg` governed by Sections 8.6–8.7. Diagnostics must be opt-in and locally reviewable before export, with sensitive content excluded by construction.

Users are responsible for selecting media they are authorized to acquire and analyze and for obtaining necessary consent. The application records their attestation but does not purport to verify legal ownership. This boundary does not eliminate the maintainers' responsibility for secure design, accurate representations, lawful distribution, or remediation of known software defects.

Public availability is not equivalent to permission to create a biometric or behavioral dataset. YouTube’s terms restrict several forms of automated access and harvesting, and copyright remains applicable. [YouTube Terms](https://www.youtube.com/static?template=terms), [YouTube copyright guidance](https://support.google.com/youtube/answer/2797466)

Representative regulatory concerns include:

- The U.S. Department of Labor has stated that AI systems using voice, eye measurements, microexpressions, or body movements to assess truthfulness can fall within employee lie-detector restrictions. [DOL Field Assistance Bulletin 2024-1](https://www.dol.gov/sites/dolgov/files/WHD/fab/fab2024_1.pdf)
- The FTC warns that unsupported biometric accuracy claims, unexpected collection, inadequate risk assessment, poor security, and discriminatory performance may be deceptive or unfair. [FTC biometric policy statement](https://www.ftc.gov/legal-library/browse/policy-statement-federal-trade-commission-biometric-information-section-5-federal-trade-commission)
- Illinois BIPA regulates commercial collection of certain biometric identifiers, including face-geometry scans, with notice, consent, retention, security, and private-action provisions. [Illinois BIPA](https://www.ilga.gov/Legislation/ILCS/Articles?ActID=3004&ChapterID=57)
- Texas regulates commercial capture of biometric identifiers, including face geometry. [Texas Business and Commerce Code Chapter 503](https://statutes.capitol.texas.gov/Docs/BC/pdf/BC.503.pdf)
- The EU AI Act records serious concerns about the reliability, specificity, and generalizability of biometric emotion/intention inference and prohibits or classifies certain applications according to context. [EU AI Act](https://eur-lex.europa.eu/eli/reg/2024/1689/oj?locale=en)

Exact applicability depends on jurisdiction, consent, commercial purpose, extracted features, deployment context, and how outputs affect people. Legal review is required before collecting public-personality data or moving beyond consented research.

Publishing an incorrect “liar” classification could also create defamation, false-light, discrimination, due-process, and reputational harms. Disclaimers do not repair a scientifically unsupported accusation.

## 13. Principal risks and mitigations

| Risk | Primary mitigation | Stop condition |
|---|---|---|
| No stable signal exists for a subject | Prospective session-held-out testing | Performance remains at chance or unstable |
| Model detects stress rather than deception | Truthful-stress and calm-deception controls | Stress controls erase performance |
| Topic, source, or device leakage | Counterbalancing, metadata baseline, grouped splits | Metadata/source predicts labels |
| Incorrect intent labels | Evidence provenance and ambiguous/unknown class | Labels cannot be independently supported |
| Small effective sample size | Count sessions, perform pilot-based power analysis | Confidence intervals remain too wide |
| Distribution shift | Context-quality and OOD gates | T+1 falls outside validated conditions |
| Misleading percentage | Separate calibration and reliability testing | Prospective calibration fails |
| Behavioral drift over time | Periodic locked revalidation | Model degrades across prospectively ordered sessions |
| Missing or wrong subject/audio | Face-speaker association and quality thresholds | Identity confidence is insufficient |
| Automation bias and false accusation | Separate outputs, abstention, prohibited-use policy | Users treat scores as proof |
| Privacy or biometric harm | Consent, local processing, encryption, deletion | Consent or lawful basis is absent |
| Unauthorized or opaque media acquisition | User-initiated local/direct-URL inputs, rights attestation, no site ripper | The source or consent cannot be supported |
| Malicious or misleading URL content | Scheme/redirect/size limits, `.part` staging, content validation, sandboxed decoding | Response is not a valid supported media file |
| Corrupted resumed download | Strong validator plus `Range`/`If-Range` and exact `206 Content-Range` checks | The resource changed or safe resume is unsupported |
| Cancelled job keeps files locked | Bounded worker-tree shutdown, stream disposal, and post-cancel handle verification | The job folder cannot be made inactive and deletable |
| Accidental publication of user data | Separate private data roots, release exclusions, synthetic fixtures, pre-release scans | Any user media or person-specific artifact enters a public package |
| Original media enters a private model export | Strict export allowlist, prohibited-entry scan, and final archive revalidation | Any media, media derivative, transcript, path, or URL is detected |
| Wrong-person or incompatible package import | Authenticated encryption, profile-lineage, schema, dependency, checksum, size, and operator validation | Identity, integrity, or compatibility cannot be established |
| Trainable package mixes incompatible features | Frozen feature contracts or installed compatible legacy pipeline | Feature schemas or preprocessing hashes differ |
| Unauthorized derived-data exchange | Explicit sharing attestation, encryption, audit record, and recipient guidance | Authorization to share models/features is absent |
| Failed update replaces a working model | Immutable versions and atomic promotion after validation | Candidate fails any integrity, parity, eligibility, or validation gate |
| Dependency or licensing risk | Pin versions and review all licenses | Required component cannot be lawfully distributed |

## 14. Delivery roadmap

### Milestone 1 — Research workbench foundation

- Create the C#/.NET solution.
- Implement the profile-centered Main, Add Profile, Edit Profile, and Query Profile shell plus project, subject, consent, and session management.
- Add user-selected profile workspace/download roots, clearly named job folders, verified truthful/intentional-deception media lists, archive state, and background status.
- Add the two approved MP4 ingest paths: explicit local-file/folder-list selection and direct HTTP(S) media download to a staged, resumable `.part` file.
- Add URL sanitization, rights attestation, byte-range resume, download limits, MP4 content validation, content-hashed non-mutating storage, and explicit rejection of webpages, playlists, streaming manifests, other containers, and site ripping.
- Add FFmpeg probing, proxies, audit logging, safe worker cancellation with handle-release verification, and verified withdrawal/deletion handling.
- Build synchronized playback, waveform, and transcript editing.
- Establish the open-source license, dependency register, third-party notices, model-license registry, contribution provenance policy, and release SBOM.

**No deception score is produced.**

### Milestone 2 — Reproducible feature extraction

- Add subject face selection and tracking review.
- Add visual, audio, speaker, ASR, and quality workers.
- Store versioned Parquet features and artifact manifests.
- Add worker cancellation, restart, caching, and deterministic contracts.
- Benchmark a declared 30-second reference MP4 on reference hardware, record stage timings, and implement learned local ETA reporting.

### Milestone 3 — Truthful-baseline deviation mode

- Build within-person baselines.
- Display deviation and recording-quality results.
- Implement context/OOD warnings.

**Still no truth probability.**

### Milestone 4 — Controlled paired data collection

- Finalize consented experimental protocol.
- Collect truthful, deceptive, stressful-truth, and calm-deception sessions.
- Record topic, prompt, interviewer, stakes, rehearsal, device, and environment.
- Conduct pilot-based sample-size planning.

### Milestone 5 — Baselines and grouped evaluation

- Train class-prior, metadata-only, unimodal, and fused models.
- Add grouped nested validation and leakage tests.
- Report discrimination, calibration, uncertainty, and ablations.

### Milestone 6 — Frozen local inference

- Export accepted preprocessing and models to ONNX.
- Implement C# ONNX inference.
- Verify Python/C# numerical parity.
- Implement query-only and trainable `.vwpkg` import/export, immutable lineage, compatibility checks, and atomic model promotion.
- Package the model schema, preprocessing, calibration, OOD/quality rules, and model card as one encrypted ZIP-based file.
- Add exact-size review, checksums, strict allowlisting, prohibited-media scans, hostile-archive import hardening, and colleague portability tests.

### Milestone 7 — Prospective T+1 confirmatory program

- Freeze and preferably preregister the protocol.
- Analyze a fully unseen future session as the first workflow and discrimination test.
- Accumulate many independent prospective sessions/outcomes spanning the relevant score range before making a probability-calibration claim.
- Open each session’s labels only after its predictions are locked.
- Evaluate prespecified go/no-go criteria.

### Milestone 8 — Publish, reframe, or stop

- If discrimination and calibration replicate, continue narrowly under the validated protocol.
- If only baseline deviation is stable, retain the behavioral-change workbench.
- If behavioral inference fails but claim analysis succeeds, pivot to an evidence-based claim-verification assistant.
- If neither provides useful validated performance, stop rather than manufacture a percentage.

## 15. Go/no-go criteria

The pilot is exploratory: use it to estimate variability, effect size, feasibility, and suitable thresholds. After pilot analysis, freeze the confirmatory protocol and numeric thresholds before examining any untouched confirmatory labels. Pilot data must be excluded from confirmatory performance and calibration claims. At minimum, the project proceeds to any percentage-bearing output only if:

- labels for both classes are credible and independently supported;
- there are enough independent sessions for useful precision;
- the prospective confirmatory series exceeds the frozen class-prior and metadata-only baselines;
- confidence intervals exclude practically useless performance;
- calibration is acceptable in the target context;
- results survive modality ablations and leakage tests;
- performance is not carried by one topic, source, device, or session;
- abstention reliably captures poor-quality and out-of-distribution inputs;
- security, consent, licensing, and legal requirements are satisfied;
- an independent replication is planned before any consequential claim.

Failure is an informative research result. It should cause the system to remain a deviation tool, collect better data, pivot to claim verification, or stop.

## 16. Resolved and remaining decisions

### 16.1 Resolved product decisions

- The product name is **VerityWorkbench**.
- Version 1 accepts explicitly selected local MP4s and direct HTTP(S) MP4 media URLs only; folder selection is a reviewed explicit file list, not recursive import.
- The main workflow is Add Profile, Edit Profile, and Query Profile.
- Add/Edit Profile accepts a `.vwpkg`, curated training videos, or both, with separate verified truthful and verified intentional-deception lists.
- Each profile uses a user-selected workspace root and a user-selected download-staging root, with inspectable named subfolders.
- Recording date is user-entered display/sort metadata only and never a model feature or source of independence.
- Downloads resume when HTTP range support and strong resource validation make continuation safe.
- Processing cancellation stops workers, closes file handles, does not promote partial results, and leaves the bounded job folder available for inspection or deletion.
- Archived videos remain stored and audited but are excluded from future candidate models; permanent deletion and withdrawal are separate.
- Query results use synchronized video and clickable transcript timestamps, with strict score/calibration language and abstention.
- Two private export types exist: query-only and trainable. Every export is exactly one encrypted ZIP-based `.vwpkg` file.
- Original videos, media derivatives, raw transcripts, source URLs, and local paths are never included in a `.vwpkg`.
- A trainable package contains only models, finalized numeric features, coded labels, grouping metadata, configurations, and sanitized provenance.

### 16.2 Decisions still required

- Minimum supported Windows version and declared CPU/GPU/RAM reference targets for the WinUI 3 application.
- Final open-source license: Apache-2.0 is recommended; MPL-2.0 remains an alternative if file-level reciprocal openness is desired.
- Exact operational definition of intentional deception.
- Consent, derived-data sharing, recipient authorization, and withdrawal wording.
- Label-adjudication and compliance-review process.
- Initial subject count, controlled pilot design, and final power/precision calculation.
- Detailed wording and retention rules for the user-supplied-media rights/consent attestation.
- Exact visual/audio/text feature extractors and their commercial redistribution terms.
- Local speech-recognition model size and reference-hardware policy.
- Predeclared validation, calibration, and go/no-go thresholds.
- Portable package encryption and recovery UX, optional sender-signing/authenticity policy, and compatibility support window.
- Whether package provenance uses original media hashes or export-scoped opaque identifiers, recognizing that hashes can be linkable.
- Safe relocation behavior when a user changes an existing profile's workspace or download root.
- Whether synchronized multi-angle controlled collection enters version 1 or a later research milestone.
- Whether external claim checking is local, cloud-assisted, or deferred.

## 17. Core references

### Behavioral science

- [Bond and DePaulo — Accuracy of deception judgments](https://pubmed.ncbi.nlm.nih.gov/16859438/)
- [DePaulo et al. — Cues to deception](https://pubmed.ncbi.nlm.nih.gov/12555795/)
- [Hartwig and Bond — Why do lie-catchers fail?](https://pubmed.ncbi.nlm.nih.gov/21707129/)
- [Vrij et al. — Reading lies review](https://doi.org/10.1146/annurev-psych-010418-103135)
- [Barrett et al. — Facial expressions and emotion](https://pubmed.ncbi.nlm.nih.gov/31313636/)
- [Jordan et al. — Microexpression training experiment](https://doi.org/10.1002/jip.1532)
- [Registered high-stakes replication](https://pubmed.ncbi.nlm.nih.gov/40146559/)

### Personalization, audio, language, and multimodal ML

- [Person-specific facial EMG study](https://pmc.ncbi.nlm.nih.gov/articles/PMC8671780/)
- [Webcam and smartwatch study](https://pmc.ncbi.nlm.nih.gov/articles/PMC10141812/)
- [Voice-stress evaluation](https://pubmed.ncbi.nlm.nih.gov/19432740/)
- [Computer-detected linguistic cues meta-analysis](https://pubmed.ncbi.nlm.nih.gov/25387767/)
- [UNIDECOR cross-corpus study](https://aclanthology.org/2023.wassa-1.5/)
- [DOLOS audiovisual dataset and model](https://openaccess.thecvf.com/content/ICCV2023/html/Guo_Audio-Visual_Deception_Detection_DOLOS_Dataset_and_Parameter-Efficient_Crossmodal_Learning_ICCV_2023_paper.html)
- [Cross-domain audiovisual benchmark](https://arxiv.org/html/2405.06995)
- [Cross-database facial study](https://doi.org/10.1007/s00521-024-09811-x)
- [ACL 2025 multimodal evaluation](https://aclanthology.org/2025.acl-long.1497/)
- [FACSCaps pose-independent facial action coding](https://openaccess.thecvf.com/content_cvpr_2018_workshops/papers/w41/Ertugrul_FACSCaps_Pose-Independent_Facial_CVPR_2018_paper.pdf)
- [3D-aware facial landmarks with multi-view consistency](https://openaccess.thecvf.com/content/CVPR2023/papers/Zeng_3D-Aware_Facial_Landmark_Detection_via_Multi-View_Consistent_Training_on_Synthetic_CVPR_2023_paper.pdf)

### Calibration, implementation, and governance

- [Guo et al. — Neural-network calibration](https://proceedings.mlr.press/v70/guo17a.html)
- [Ovadia et al. — Uncertainty under dataset shift](https://proceedings.neurips.cc/paper_files/paper/2019/hash/8558cb408c1d76621371888657d2eb1d-Abstract.html)
- [Riley et al. — Minimum sample size for external validation of a binary prediction model](https://onlinelibrary.wiley.com/doi/10.1002/sim.9025)
- [NIST AI Risk Management Framework](https://airc.nist.gov/airmf-resources/airmf/3-sec-characteristics/)
- [FFmpeg documentation](https://www.ffmpeg.org/documentation.html)
- [RFC 9110 — HTTP range and conditional request semantics](https://www.rfc-editor.org/rfc/rfc9110.html)
- [.NET RangeHeaderValue](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.headers.rangeheadervalue?view=net-10.0)
- [ONNX Runtime C# documentation](https://onnxruntime.ai/docs/get-started/with-csharp.html)
- [ML.NET documentation](https://learn.microsoft.com/en-us/dotnet/machine-learning/)
- [WinUI 3 documentation](https://learn.microsoft.com/en-us/windows/apps/winui/winui3/)
- [OpenSeeFace code and model license](https://github.com/emilianavt/OpenSeeFace)
- [Silero VAD](https://github.com/snakers4/silero-vad)
- [Whisper code and model weights](https://github.com/openai/whisper)
- [librosa license](https://github.com/librosa/librosa/blob/main/LICENSE.md)
- [FFmpeg license](https://github.com/FFmpeg/FFmpeg/blob/master/LICENSE.md)
- [GitHub repository licensing guidance](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/licensing-a-repository)
- [Apache License 2.0](https://www.apache.org/licenses/LICENSE-2.0.html)
- [Open Source Definition](https://opensource.org/osd)
- [Open Source AI Definition 1.0](https://opensource.org/ai/open-source-ai-definition)
- [SPDX software-bill-of-materials overview](https://spdx.dev/about/overview/)
- [EU AI Act](https://eur-lex.europa.eu/eli/reg/2024/1689/oj?locale=en)
- [FTC biometric-information policy statement](https://www.ftc.gov/legal-library/browse/policy-statement-federal-trade-commission-biometric-information-section-5-federal-trade-commission)

---

## Final project position

The revolutionary opportunity is not a machine that reads truth directly from a face. It is a rigorous, transparent system that asks whether a person-specific multimodal signal exists, refuses to overclaim when it does not, and separates behavioral change from evidence about the spoken claims.

The software is buildable. Whether it can produce a meaningful deception probability for any particular subject remains an empirical question that the application itself must be designed to test honestly.

The software itself should be open, original, reproducible, and independently auditable. Its human data should not be. Each user brings authorized input through an explicit local MP4 or direct media URL, all acquisition and processing resolves to private local artifacts, and each resulting person-specific model remains under that user's control. A model or allowlisted numeric training state leaves a machine only through an explicit encrypted `.vwpkg` export to an authorized recipient; media and media derivatives never do. The project supplies the method—not the people, videos, labels, or trained identities.
