# Contributing to FindIt

Thanks for your interest in FindIt. Bug reports, ideas, documentation fixes and code are all welcome.

## Reporting bugs and ideas

Open an [issue](https://github.com/adilzubair/FindIt/issues/new/choose) using the bug or feature template. For bugs, include the FindIt version, Windows version, the drive's file system (NTFS, exFAT or FAT32), what you did and what happened.

Please **do not attach personal photos, videos, documents or archive files** to issues. If a specific file triggers the problem, describe its type, size and codec, or share a non-personal file that reproduces it.

Security problems should be reported privately; see [SECURITY.md](SECURITY.md).

## Making changes

1. Open an issue first for anything larger than a small fix, so we can agree on the approach.
2. Fork the repository and create a branch from `main`.
3. Follow [docs/development.md](docs/development.md) to build and run tests. For managed-only changes, `dotnet build FindIt.slnx -c Release` and `dotnet test tests/FindIt.Tests/FindIt.Tests.csproj -c Release --no-build` are enough.
4. Add or update tests for what you changed, and update the docs and the `Unreleased` section of [CHANGELOG.md](CHANGELOG.md) when behavior changes.
5. Open a pull request describing the change and how you tested it.

## Guidelines

- **Originals are read-only.** Nothing may write to, move or delete a user's files. Preparation writes only beneath `FindIt/archive`.
- **Offline and private.** No telemetry, no automatic network access and nothing written to the host PC beyond the disposable caches under `%LOCALAPPDATA%\FindIt`.
- **Keep the UI responsive.** Inference, extraction and native code run in the worker process, not in the WPF application.
- **Pin dependencies.** New downloads need a fixed URL and SHA-256, and their licenses must be added to [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
- **Be explicit about evidence.** Performance or relevance claims need a reproducible measurement and its limits, as in [docs/validation.md](docs/validation.md).
- Match the style of the surrounding code; the build treats analyzer warnings seriously and should stay at zero warnings.

## Code of conduct

This project follows the [Code of Conduct](CODE_OF_CONDUCT.md). By participating you agree to uphold it.

## License

By contributing, you agree that your contributions are licensed under the [MIT License](LICENSE).
