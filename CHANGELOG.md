# Changelog

All notable changes to FindIt are recorded here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- A 38-second demo video at the top of the README, recorded from the real app on a demo library ([credits](docs/media/CREDITS.md)).
- The project website at [findit.muhamedadil.com](https://findit.muhamedadil.com/), built from `website/` and deployed to GitHub Pages. Visitors can search a sample drive with a small model that runs in the browser. Its download links follow the newest published release.

## [0.1.0-preview.9] - 2026-10-23

First version prepared for public release. Earlier previews were distributed privately for testing.

### Added

- Native startup window: the launcher shows copy and verification progress before reading the drive, can be cancelled, repairs an incomplete cache on the next launch and brings the running window forward on repeated clicks.
- Search-first Windows 11 interface: custom title bar with search, navigation rail, Home with starter searches, recent files and people, a uniform result grid that says why each file matched, a preview that becomes a sheet on narrow windows, full-page people with inline yes/no review, a Drive page and Settings.
- Stable ranked search sessions with continuation pages and **Search more**, and scrolling that appends results instead of paging.
- Video preview with a seek bar that marks the search's own matches, joins nearby matches into moments, shows the nearest prepared frame under the pointer, and supports previous/next match, mute and keyboard control.
- Every video result now shows a picture, including matches on speech, sound or a scene window.
- Measured preparation progress and remaining-time estimate.
- Recent searches and the last prepared folders are remembered on the drive.
- Reader workers that crash are restarted and the request retried once.

### Changed

- Preparation commits files in batches, spaces out checkpoints as the library grows, passes media to the encoder from memory and runs Whisper once per recording.
- Model checksums are remembered per PC for the exact file, and the query encoder loads in the background, so the first search by meaning no longer waits for a slow drive.
- Gallery thumbnails decode at their displayed size with a least-recently-used cache; selecting files and resizing no longer rebuild every row.
- The reader's ANN search expansion is 8,192, chosen on a separate tuning split and evaluated on untouched queries.
- Small filtered searches use exact search without loading the ANN index; large indexes stream from disk with a lower memory peak.

### Fixed

- FAT32 drives no longer re-hash every archive artifact at each launch (worker ready time on the test stick: 30 s → 0.3 s).
- Previews are blocked when an original's size or modification time changed since preparation.
- Resumed preparation and sampled-person playback edge cases.
- The build pins FFmpeg to BtbN's month-end `n8.1.3-9-g29e619e767` LGPL build. The previously pinned daily build no longer matched its checksum upstream and would have been deleted. The third-party notice now states FFmpeg's license as LGPL-3.0-or-later, matching the build's `--enable-version3` configuration.

## [0.1.0-preview.1] - 2026-10-21

### Added

- Portable offline search for photos, video, audio and documents with a native launcher, WPF application and isolated reader, preparer and playback workers.
- EmbeddingGemma text, image, audio and video embeddings with USearch ANN indexes, SQLite FTS5 keyword search and reciprocal rank fusion.
- Face grouping, reference enrollment and timestamp-constrained people search.
- Resumable, crash-safe preparation with immutable checksummed generations.
- Signed offline updates, reproducible builds with pinned dependencies, benchmarks and recovery validation tools.

[Unreleased]: https://github.com/adilzubair/FindIt/compare/v0.1.0-preview.9...HEAD
[0.1.0-preview.9]: https://github.com/adilzubair/FindIt/releases/tag/v0.1.0-preview.9
[0.1.0-preview.1]: https://github.com/adilzubair/FindIt/commit/a5e3501
