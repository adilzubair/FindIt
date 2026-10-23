# Security policy

## Supported versions

FindIt is in preview. Security fixes are made on `main` and shipped in the next release; only the latest release is supported.

## Reporting a vulnerability

Please **do not open a public issue** for security problems.

Report them privately through GitHub's [private vulnerability reporting](https://github.com/adilzubair/FindIt/security/advisories/new). Include the affected version, steps to reproduce and the impact you expect. You should receive a response within a week, and you will be credited in the release notes unless you prefer otherwise.

## Scope

Areas of particular interest:

- Path handling: traversal, junctions, symlinks or alternate data streams that let preparation or preview read or write outside the selected drive and `FindIt/archive`.
- The launcher's runtime cache and `package.sha256` verification.
- Offline update verification (signature, file hashes, version and path checks in `PortableUpdateManager`).
- Parsing of untrusted media and documents in the worker process, including the bundled native decoders.
- The framed JSON-RPC protocol between the application and its workers.

Vulnerabilities in bundled third-party components (FFmpeg, LibVLC, PDFium, OpenCV, Tesseract, llama.cpp and others) should also be reported upstream; we will update the pinned versions once a fix is available.
