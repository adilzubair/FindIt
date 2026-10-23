# Architecture

## Processes and dependency loading

`FindIt.exe` is a small native launcher. It selects `FindIt/app/current.txt`, copies the versioned public runtime into a content-addressed host cache, validates `package.sha256`, and starts the cached WPF application with the original drive root. The UI does not run embedding or face inference.

`FindIt.Worker.exe` has reader, preparer, and preview roles. JSON-RPC messages are UTF-8 with a four-byte little-endian length prefix and a bounded frame size. Responses carry request IDs; preparation progress has the originating request ID. Cancellation is sent only after the request has entered the stream. EOF cancels work and closes models/indexes. Native diagnostics use stderr so they cannot corrupt the protocol.

When the library can be searched by meaning, the reader starts loading the text backbone in the background as soon as it opens, so the first query does not wait for a slow drive. It keeps compatible query encoding resident and maps immutable vector data. Each model is SHA-256 verified before it is loaded; a successful hash is remembered in `%LOCALAPPDATA%\FindIt\verification` for that exact file identity (volume, file index, size, creation and write time), and any change verifies the file again. The preparer loads media/face/OCR dependencies as needed. Vulkan is eligible only after a timed CPU/GPU numerical comparison and meaningful measured speedup; CPU remains the fallback. The preview worker alone hosts LibVLC. Native failures are contained by process boundaries. When the reader process dies, the client starts a new reader and retries the request once (a save is reported rather than repeated); a reader that dies again within ten seconds is reported instead of restarted. The reader keeps one read-only catalog connection for the current generation, so SQLite's page cache survives between searches and previews; a request that finds it busy opens its own, and the connection closes when the generation changes, on any SQLite error, or when the reader ends.

The preparer passes images, video frames, PDF pages and audio windows to the native encoder from memory (`fi_embed_media_buffer`, `fi_embed_frame_buffers`, ABI v1.1). Both paths decode through mtmd's buffer loader, so vectors are identical; an older native build without these exports falls back to scratch files. Each preparer keeps one private scratch folder under `work/runtime`, created on first use, emptied after each original and removed on exit. Whisper transcribes each recording in parts of up to two hours rather than per embedding window.

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

Each original is prepared inside a SQLite savepoint, and originals commit together in batches of up to 256 files or two seconds, so small files do not each pay several forced flushes. A failed original rolls back only its savepoint and its own pack tail; an interruption re-prepares only the uncommitted batch. Payload packs are flushed before their references commit. File size/mtime are confirmed again after extraction. Intermediate checkpoints publish committed files after the first file and then every 100 changed files or a quarter of the published library, whichever is larger, or every five minutes when that keeps checkpoint work to about a tenth of preparation time. They use SQLite `quick_check`; the final publication also merges the text index and runs a full `integrity_check`. Progress is measured in bytes of originals against a background census of the selected folders; the remaining time is estimated from the rate of extracted bytes once twenty seconds of extraction have been measured. The folders of the last preparation are saved in `people.sqlite`, so Update and Resume start from them. A failed update retains previous prepared content and records an issue; a new unreadable file still receives a filename entry. The explicit content verification path performs stronger hashing.

Preparation never mutates a published catalog or appends to published packs. Publication writes a complete catalog, canonical vector-record table and derived ANN files, checks integrity, hashes artifacts, closes SQLite, moves the snapshot, then changes the pointer. SQLite uses a rollback journal, so published generations have no missing WAL dependency. Recovery validates the pointer and required artifacts, falling back to previous complete generations after corruption or interruption.

On Windows, SQLite connections use its built-in `win32-longpath` VFS, including attached databases, so catalog paths and journal suffixes can exceed MAX_PATH while retaining file locking. Atomic publication retries temporary Windows sharing/access failures with bounded cancellable delays and removes only its own failed temporary file. Writer-locked maintenance collects abandoned checkpoint directories without changing retained generations.

Canonical records contain segment ID, pack ordinal and byte offset. Vectors are FP16, normalized after truncation to 512 dimensions. Derived USearch HNSW indexes use cosine distance, FP16 nodes, connectivity 16 and search expansion 128. Compatible updates retain one base plus one current delta; changes reaching 10% of the base or 100,000 records trigger compaction. A live-ID filter removes tombstones from the retained base. Collection protects current/previous generation references, referenced older base indexes, active work, and user portraits. Originals and model files are never collection candidates.

The C ABI uses opaque handles and error/status returns. Exceptions are caught within the native boundary. Native buffers have explicit ownership. Cancellation is observed during inference and in bounded managed loops.

## Retrieval and people

Filename, path, and text use SQLite FTS5 plus literal path matching. Semantic queries use the same tokenizer, task prefixes, pooling, preprocessing, output dimensions and pinned quantization as preparation. The encoder fingerprint prevents comparison against incompatible vectors. Keyword retrieval remains available after a semantic runtime failure.

Type/folder/date/people constraints are applied during candidate retrieval. Small filtered sets use canonical exact cosine search; larger sets use USearch's filtered search. Base and delta results merge by distance, then lexical and semantic rankings fuse using reciprocal rank fusion. Files group their strongest matching segments, each carrying its rank within the file; the preview opens at the strongest. Only sampled video frames store a picture, so a scene window, speech or sound match takes the frame sampled nearest its start (new catalogs index segments by file and time for this lookup). Semantic scores are not shown as probability percentages.

Face vectors remain separate from scene vectors. Names and explicit corrections live in the overlay and do not require scene re-embedding. Unknown faces are clustered conservatively, uncertain memberships remain reviewable, and rejected memberships remain excluded. People queries use each observation's sampled timestamp, including when reading older catalogs with broad face intervals. Multiple people must occur in the same sampled image. Search results carry the context frame, portrait, and observation ID; preview validates current membership before opening that exact timestamp. Candidate results never verify whether a visible person is driving, riding, or performing another role.

Video extraction compares small decoded active-picture thumbnails to detect scene cuts. New samples represent instants; temporal embeddings use eight-second windows with four-second overlap within a detected shot and shorter tails when at least two regular frames are available. Frames from different detected shots never enter the same joint encoding. Cut detection and finite sampling cannot guarantee all edits or appearances are found. Extraction policy is fingerprinted independently of the encoder; only an explicit preparation applies a changed policy to existing files.

## Interface and bounds

WPF uses semantic light/dark resources, the system Segoe UI Variable typeface (Segoe UI on Windows 10) and Segoe Fluent Icons or MDL2 Assets glyphs. The contact sheet virtualizes fixed-height rows; metadata is paged independently of visual virtualization. Pages of 256 results are appended as the user nears the end, up to 16 pages at once; Previous and Next move beyond that. When preparation publishes a new generation or a search session expires, the same search runs again and reloads as many results as were shown. Recent searches are stored in `people.sqlite` on the drive, never on the PC. Selection follows stable file IDs. The video preview lists the search's own matches, joining matches less than 1.5 seconds apart into one moment, and marks them above a seek bar that seeks on click or drag and shows the nearest prepared frame under the pointer. A paused player draws nothing after opening or seeking, so the nearest prepared frame stands in until playback runs. Decoded thumbnails have a 64 MiB cache, and only the selected media plays. HWND video stays rectangular with controls outside its native surface. Drive removal clears search readiness, cancels work, and leaves a reconnectable UI.

The implementation is intentionally explicit about remaining validation. The design's RAM/latency/relevance/identity figures are acceptance targets, not properties inferred from architecture. See release validation for the outstanding evidence.
