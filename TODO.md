# VerityWorkbench TODO

## Future Android query application

- [ ] Keep the query-only `.vwpkg` format platform-neutral, including the model, preprocessing contract, feature-version metadata, calibration context, compatibility requirements, and integrity information.
- [ ] Build an Android query application only after Windows training and query inference are working and validated.
- [ ] Support importing a compatible query-only `.vwpkg` and evaluating locally captured or selected camera video on-device where practical.
- [ ] Preserve the Windows safeguards on Android: matched-context checks, media-quality/applicability reporting, uncertainty, abstention, and no claim of factual truth.
- [ ] Verify preprocessing and inference parity between Windows and Android using identical reference media before releasing Android query functionality.
- [ ] Keep subject media and derived biometric data local by default; do not require cloud processing for the Android query path.
