# Third-party components

FindIt uses the following third-party software and model artifacts. Preserve the accompanying `licenses` folder when distributing a portable package. Exact downloaded binary hashes/source revisions are pinned in `scripts/Fetch-Dependencies.ps1`, `scripts/Build-Native.ps1`, NuGet project references, and `models/manifest.json`.

| Component | Provenance / license |
|---|---|
| .NET / WPF | [dotnet/runtime](https://github.com/dotnet/runtime), [dotnet/wpf](https://github.com/dotnet/wpf); MIT and bundled third-party notices |
| Microsoft.Data.Sqlite / SQLitePCLRaw / SQLite | Microsoft .NET packages, [SQLitePCL.raw](https://github.com/ericsink/SQLitePCL.raw); MIT / Apache-2.0; SQLite public domain |
| llama.cpp / ggml / libmtmd | [ggml-org/llama.cpp](https://github.com/ggml-org/llama.cpp), revision `4d756bc72bf00a4aacf410ae15a2d315f3db400d`; MIT; local noncausal no-logits patch in build script |
| EmbeddingGemma 2 GGUF | [ggml-org/embeddinggemma-2-GGUF](https://huggingface.co/ggml-org/embeddinggemma-2-GGUF), pinned model revision; Apache-2.0 as recorded by the artifact/model manifest |
| USearch | [unum-cloud/USearch](https://github.com/unum-cloud/USearch), revision `7a4abfb2033935498256044e01bc0049ec80d76c`; Apache-2.0 |
| OpenCV / OpenCvSharp | [opencv/opencv](https://github.com/opencv/opencv), [shimat/opencvsharp](https://github.com/shimat/opencvsharp); Apache-2.0 |
| YuNet / SFace | [opencv/opencv_zoo](https://github.com/opencv/opencv_zoo); detector MIT; recognizer Apache-2.0 |
| whisper.cpp / Whisper small | [ggml-org/whisper.cpp](https://github.com/ggml-org/whisper.cpp) v1.9.5, [openai/whisper](https://github.com/openai/whisper); MIT |
| Silero VAD | [snakers4/silero-vad](https://github.com/snakers4/silero-vad), [GGML conversion](https://huggingface.co/ggml-org/whisper-vad); MIT |
| FFmpeg | LGPL build `n8.1.3-18-g46d899d7a0` from [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds/releases/tag/autobuild-2026-10-20-13-08); LGPL-2.1-or-later; build scripts and source provenance in `licenses/SOURCES.txt` |
| LibVLC | [VideoLAN.LibVLC.Windows](https://code.videolan.org/videolan/libvlc-nuget) 3.0.24; LGPL-2.1-or-later package; modular codecs/plugins carry their own accompanying terms |
| PDFium | Chromium PDFium build 8111 from [bblanchon/pdfium-binaries](https://github.com/bblanchon/pdfium-binaries); BSD-style and accompanying third-party notices |
| Tesseract / Leptonica / tessdata | [tesseract-ocr](https://github.com/tesseract-ocr), [charlesw/tesseract](https://github.com/charlesw/tesseract), [DanBloomberg/leptonica](https://github.com/DanBloomberg/leptonica); Apache-2.0 / BSD-style |
| IBM Plex Sans | [IBM/plex](https://github.com/IBM/plex); SIL Open Font License 1.1, embedded font source/license in the application source |
| LLVM-MinGW runtimes | [mstorsjo/llvm-mingw](https://github.com/mstorsjo/llvm-mingw); accompanying runtime notices |
| Microsoft C++ desktop runtime | Official Microsoft.VCLibs.x64.14.00.Desktop artifact; Microsoft redistribution terms; extracted app-locally, no installation |

The native libraries remain separate and replaceable in the published folder. The development preview is unsigned. Public distribution requires checking all transitive codec/plugin notices and corresponding source/build-material obligations for the actual produced package. The repository's fetch scripts and source links document provenance; they are not a claim that the public-release redistribution gate has already passed.

DigUp informed the architecture and model compatibility spike. FindIt implements its own Windows UI, portable catalog, worker protocol, and storage design. The llama.cpp memory patch follows the noncausal embedding allocation issue documented by [DigUp](https://github.com/ARahim3/DigUp).
