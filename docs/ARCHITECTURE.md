# Architecture

## Processes and dependency loading

`FindIt.exe` is a small native launcher. It selects `FindIt/app/current.txt`, copies the versioned public runtime into a content-addressed host cache, validates `package.sha256`, and starts the cached WPF application with the original drive root. The UI does not run embedding or face inference.

`FindIt.Worker.exe` has reader, preparer, and preview roles. JSON-RPC messages are UTF-8 with a four-byte little-endian length prefix and a bounded frame size. Responses carry request IDs; preparation progress has the originating request ID. Cancellation is sent only after the request has entered the stream. EOF cancels work and closes models/indexes. Native diagnostics use stderr so they cannot corrupt the protocol.

The reader opens the text backbone on demand, keeps compatible query encoding resident, and maps immutable vector data. The preparer loads media/face/OCR dependencies as needed. Vulkan is eligible only after a timed CPU/GPU numerical comparison and meaningful measured speedup; CPU remains the fallback. The preview worker alone hosts LibVLC. Native failures are contained by process boundaries.

## Drive layout

```text
FindIt.exe
FindIt/
  app/current.txt
  app/0.1.0-preview.4/       self-contained application and native tools
  models/                   hash-verified model artifacts
  archive/
    current.json            checksummed pointer to a complete generation
    generations/<sequence>/ catalog.sqlite, vector records, indexes, manifest
    packs/                  immutable preview/vector blobs
    staging/                current resumable preparation catalog and state
    work/runtime/           temporary extraction files
    people.sqlite           references, corrections, preferences
```

Paths inside catalogs/manifests are relative to the selected root. A UUID identifies the library. Path resolution rejects traversal, alternate data streams, junctions, and symlinks. The implementation's `packs/staging/people.sqlite` names serve the `objects/work/user.sqlite` roles from the design.

## Storage and preparation

Each original is prepared inside a SQLite transaction. Payload packs are flushed before their references commit. File size/mtime are confirmed again after extraction. A failed update retains previous prepared content and records an issue; a new unreadable file still receives a filename entry. The explicit content verification path performs stronger hashing.

Preparation never mutates a published catalog or appends to published packs. Publication writes a complete catalog, canonical vector-record table and derived ANN files, checks integrity, hashes artifacts, closes SQLite, moves the snapshot, then changes the pointer. SQLite uses a rollback journal, so published generations have no missing WAL dependency. Recovery validates the pointer and required artifacts, falling back to previous complete generations after corruption or interruption.

On Windows, SQLite connections use its built-in `win32-longpath` VFS, including attached databases, so catalog paths and journal suffixes can exceed MAX_PATH while retaining file locking. Atomic publication retries temporary Windows sharing/access failures with bounded cancellable delays and removes only its own failed temporary file. Writer-locked maintenance collects abandoned checkpoint directories without changing retained generations.

Canonical records contain segment ID, pack ordinal and byte offset. Vectors are FP16, normalized after truncation to 512 dimensions. Derived USearch HNSW indexes use cosine distance, FP16 nodes, connectivity 16 and search expansion 128. Compatible updates retain one base plus one current delta; changes reaching 10% of the base or 100,000 records trigger compaction. A live-ID filter removes tombstones from the retained base. Collection protects current/previous generation references, referenced older base indexes, active work, and user portraits. Originals and model files are never collection candidates.

The C ABI uses opaque handles and error/status returns. Exceptions are caught within the native boundary. Native buffers have explicit ownership. Cancellation is observed during inference and in bounded managed loops.

## Retrieval and people

Filename, path, and text use SQLite FTS5 plus literal path matching. Semantic queries use the same tokenizer, task prefixes, pooling, preprocessing, output dimensions and pinned quantization as preparation. The encoder fingerprint prevents comparison against incompatible vectors. Keyword retrieval remains available after a semantic runtime failure.

Type/folder/date/people constraints are applied during candidate retrieval. Small filtered sets use canonical exact cosine search; larger sets use USearch's filtered search. Base and delta results merge by distance, then lexical and semantic rankings fuse using reciprocal rank fusion. Files group their strongest matching segments. Semantic scores are not shown as probability percentages.

Face vectors remain separate from scene vectors. Names and explicit corrections live in the overlay and do not require scene re-embedding. Unknown faces are clustered conservatively, uncertain memberships remain reviewable, and rejected memberships remain excluded. People queries use each observation's sampled timestamp, including when reading older catalogs with broad face intervals. Multiple people must occur in the same sampled image. Search results carry the context frame, portrait, and observation ID; preview validates current membership before opening that exact timestamp. Candidate results never verify whether a visible person is driving, riding, or performing another role.

Video extraction compares small decoded active-picture thumbnails to detect scene cuts. New samples represent instants; temporal embeddings use eight-second windows with four-second overlap within a detected shot and shorter tails when at least two regular frames are available. Frames from different detected shots never enter the same joint encoding. Cut detection and finite sampling cannot guarantee all edits or appearances are found. Extraction policy is fingerprinted independently of the encoder; only an explicit preparation applies a changed policy to existing files.

## Interface and bounds

WPF uses semantic light/dark resources, the system Segoe UI Variable typeface (Segoe UI on Windows 10) and Segoe Fluent Icons or MDL2 Assets glyphs. The contact sheet virtualizes fixed-height rows; metadata is paged independently of visual virtualization. Selection follows stable file IDs. Decoded thumbnails have a 64 MiB cache, and only the selected media plays. HWND video stays rectangular with controls outside its native surface. Drive removal clears search readiness, cancels work, and leaves a reconnectable UI.

The implementation is intentionally explicit about remaining validation. The design's RAM/latency/relevance/identity figures are acceptance targets, not properties inferred from architecture. See release validation for the outstanding evidence.
