# People evidence and media timing

Person filters refer to accepted face observations. An observation establishes a sampled face image at one presentation timestamp; it does not establish continuous presence between samples, an action, a role, or a speaker. Video face samples are represented by a one-millisecond interval. The reader treats observations in older, broader frame intervals as samples at their recorded start time too.

When several people are selected, all must have accepted observations in the same sampled image or at the same video presentation timestamp. Finding two people in separate shots of one video or one broad clip is insufficient.

Search moments optionally carry `PersonEvidence`: the person and observation IDs, sampled timestamp, context thumbnail, aligned face portrait, quality and explicit-confirmation state. Search tiles use the context frame. Preview requests include the people and selected observation; the reader rechecks current acceptance, file ownership and moment overlap before seeking to the face. A correction made after a search can invalidate its old evidence. The scene/passage interval remains separate from the face timestamp, so an eight-second candidate can open at a face in its middle.

Published catalogs and manifests do not change during search or preview. These wire fields are additive. Preparation includes the processor extraction fingerprint; installing the new reader opens an existing archive immediately, while **explicitly preparing again** applies the new extraction policy to unchanged files.

Before opening an original, preview compares its current byte length and UTC modification time with the prepared record. A mismatch blocks preview and asks for explicit preparation, so old face evidence cannot automatically open visibly changed media. This is a metadata check, not a content hash. Equal-size, equal-timestamp replacements require preparation with **Verify contents**.

## Preparation timeline

FFmpeg retains selected source presentation timestamps rather than retiming images with its `fps` filter. Half-second target slots limit face processing without bursts after sparse VFR gaps; regular visual samples target one-second slots. A bounded four-entry channel pairs timestamps and raw images, rawvideo output uses one encoder thread, diagnostic text is bounded, and cancellation kills and drains the decoder. Missing or invalid frame timestamps fail that file rather than pairing a later timestamp with the wrong image.

Video, audio windows and transcript offsets use a common container playback origin. A video starting three seconds after audio retains that three-second delay. A video-only container whose streams all start at three seconds normalizes that shared offset. Presentation timestamps are unrelated to camera capture time. The preview host uses the bundled `avformat` demuxer to match this origin; native MP4 and Matroska demuxers exposed different clocks in the offset fixtures.

The initial person preview displays the actual prepared context image and a face crop. Older archives without a context thumbnail use their saved face crop with an explicit crop tooltip. Explicit Play or seek switches to decoded footage. A codec may show a preroll frame while paused at a cut even when its clock reports the requested time; the prepared image keeps the sampled evidence inspectable. Candidate identity/action and speaker limitations still apply. Face crops and names stay available in the inspector while footage plays.

Detected scene cuts end the previous temporal queue. Eight-second visual windows advance every four seconds within a shot; short shots and final tails with at least two regular samples also get joint embeddings. Windows are scheduled by presentation time, not by the count of decoded frames. Every encoded image lies inside its advertised half-open window, with at most eight retained frame files. Stable black borders are excluded from cut-comparison statistics. Cut detection and sampling can still miss brief appearances or edits.

Video stream duration is preferred for EOF. When it is missing, container `format.duration` can mean a length or an ending timestamp depending on the format. EOF chooses the smallest metadata candidate beyond the last retained decoded sample, falling back to that sample plus one millisecond. This is a conservative coverage estimate; ambiguous metadata does not prove complete coverage of the final unsampled interval.

## Validation

`PersonEvidenceTests`, `TemporalPeopleTests`, `PeopleReferencePolicyTests`, `ExtractionFingerprintTests`, `SceneChangeTests`, `VideoFrameTimestampTests` and `VideoWindowTests` cover stale corrections, foreign-file evidence, old catalogs, same-frame co-occurrence, explicit preparation, black borders, sparse source timelines, shared offsets and conservative EOF.

The optional native integration command uses the local thirteen-file USB fixture, which is private test media and is not included in this repository:

```powershell
FindIt.DriveCheck.exe --people-media `
  --root artifacts/people-media-fixture `
  --fixtures artifacts/usb-e2e-media `
  --worker <isolated-build>/FindIt.Worker.exe `
  --report artifacts/people-native-check.json
```

This mode only writes inside an owned workspace `artifacts` folder and refuses removable-drive roots. It retains an 18-file gallery and a report: the original fixtures plus a long uncut shot, half-second cut, offset VFR source, delayed video and delayed audio. It checks actual Gemma, face recognition, FFmpeg and Whisper integration, original-source timestamps and preview metadata. Synthetic still-photo videos provide timing ground truth, not actor/action ground truth or a representative relevance score. Playback clock, low-end hardware, large archives and held-out identity/relevance certification require their separate gates.

The app's explicit `--evidence-smoke <report.json>` mode opens a real prepared library, searches the query given by `--render-query` (default `Test Person driving` for this fixture), verifies the sampled poster/crops and paused clock, and activates the actual Play/Pause control. It never starts preparation. The separate `--paused-playback-smoke` checks clock advancement, pause, seek and stop using the isolated native player.
