using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FindIt.Client;

public sealed record UpdateCheckResult
{
    public bool VersionAvailable { get; init; }
    public string Message { get; init; } = "";
    public string? Version { get; init; }
    public string? DownloadUrl { get; init; }
}

public sealed record UpdateInstallResult
{
    public bool Installed { get; init; }
    public string Message { get; init; } = "";
    public string? Version { get; init; }
}

/// <summary>
/// Explicit checks and signed offline updates. No constructor, startup, or timer network activity.
/// An update can only add a verified immutable app version and switch the next-launch pointer.
/// Archive data, models, publisher trust configuration and the native launcher are outside its authority.
/// </summary>
public sealed class PortableUpdateManager
{
    public const string EnvelopeFileName = "findit-update.json";
    private const long MaximumPackageBytes = 4L * 1024 * 1024 * 1024;
    private const long MaximumFileBytes = 1024L * 1024 * 1024;
    private const int MaximumManifestBytes = 8 * 1024 * 1024;
    private const int MaximumFiles = 20000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly string root;
    private readonly string appDirectory;
    private readonly HttpMessageHandler? checkTransport;

    public PortableUpdateManager(string root, HttpMessageHandler? checkTransport = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        this.root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        appDirectory = Resolve("FindIt/app", false);
        this.checkTransport = checkTransport;
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var checkToken = timeout.Token;
        try
        {
            var config = await ReadConfigurationAsync(checkToken);
            if (config is null) return new() { Message = "Update checks are unavailable in this build: no publisher update source and trusted signing key are configured. No network request was made." };
            if (!TryHttps(config.ManifestUrl, out var manifestUri)) return new() { Message = "The publisher update source must be an HTTPS manifest URL. No network request was made." };
            // Validate trust before contacting even a configured endpoint.
            using (var key = ImportPublicKey(config.PublicKeyPem)) { }
            using var client = checkTransport is null ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) : new HttpClient(checkTransport, disposeHandler: false);
            client.Timeout = TimeSpan.FromSeconds(20);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("FindIt-Portable-Update-Check/1.0");
            using var response = await client.GetAsync(manifestUri, HttpCompletionOption.ResponseHeadersRead, checkToken);
            if (response.StatusCode is >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest) return new() { Message = "The configured publisher manifest redirected. Update the trusted source configuration before checking again." };
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaximumManifestBytes) throw new InvalidDataException("The publisher manifest is too large.");
            await using var stream = await response.Content.ReadAsStreamAsync(checkToken);
            var envelope = await ReadBoundedAsync(stream, MaximumManifestBytes, checkToken);
            var payload = VerifyEnvelope(envelope, config.PublicKeyPem);
            string current = await ReadCurrentAsync(checkToken);
            bool newer = CompareVersions(payload.Version, current) > 0;
            return new() { VersionAvailable = newer, Version = payload.Version, DownloadUrl = payload.DownloadUrl,
                Message = newer ? $"FindIt {payload.Version} is available. Download the publisher's signed ZIP, then choose Install offline update. It activates on the next launch." : $"FindIt {current} is up to date with the verified publisher manifest." };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new() { Message = "The update check timed out. Your current application is unchanged." }; }
        catch (Exception error) when (error is not OperationCanceledException && error is not OutOfMemoryException)
        { return new() { Message = "The update check could not be completed: " + SafeMessage(error) }; }
    }

    public async Task<UpdateInstallResult> InstallOfflineAsync(string zipPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? staging = null;
        byte[]? originalCurrent = null, originalBackup = null;
        bool backupExisted = false, pointerTouched = false;
        FileStream? updateLock = null;
        try
        {
            var config = await ReadConfigurationAsync(cancellationToken);
            if (config is null) return new() { Message = "Offline updates require the publisher's trusted public signing key in FindIt/update-source.json. This build has no update trust configuration." };
            using (var key = ImportPublicKey(config.PublicKeyPem)) { }
            string fullZip = Path.GetFullPath(zipPath);
            if (!File.Exists(fullZip) || (File.GetAttributes(fullZip) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Select a regular signed update ZIP file.");
            if (new FileInfo(fullZip).Length > MaximumPackageBytes) throw new InvalidDataException("The update ZIP exceeds the 4 GiB safety limit.");
            await using var package = new FileStream(fullZip, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var zip = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true);
            if (zip.Entries.Count > MaximumFiles + 1024) throw new InvalidDataException("The update contains too many ZIP entries.");
            var envelopeEntries = zip.Entries.Where(e => e.FullName == EnvelopeFileName).ToArray();
            if (envelopeEntries.Length != 1 || envelopeEntries[0].Length > MaximumManifestBytes) throw new InvalidDataException("The ZIP must contain one bounded findit-update.json signed envelope.");
            await using var envelopeStream = envelopeEntries[0].Open();
            var payload = VerifyEnvelope(await ReadBoundedAsync(envelopeStream, MaximumManifestBytes, cancellationToken), config.PublicKeyPem);
            // Signature validation is complete before any extraction or activation change.
            var expected = ValidatePayload(payload);
            var entries = ValidateEntries(zip, payload.Version, expected);
            string destination = Resolve($"FindIt/app/{payload.Version}", false);
            EnsureNoLinks(appDirectory);
            if (!Directory.Exists(appDirectory)) throw new DirectoryNotFoundException("The portable application's version folder is unavailable.");
            updateLock = new FileStream(Resolve("FindIt/app/.update.lock", false), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            string current = await ReadCurrentAsync(cancellationToken);
            if (CompareVersions(payload.Version, current) <= 0) return new() { Version = payload.Version, Message = "The signed package is not newer than the current application. The active version is unchanged." };
            string pointerPath = Resolve("FindIt/app/current.txt", true);
            string backupPath = Resolve("FindIt/app/current.previous.txt", false);
            originalCurrent = await ReadSmallFileAsync(pointerPath, 1024, cancellationToken);
            backupExisted = File.Exists(backupPath);
            if (backupExisted) originalBackup = await ReadSmallFileAsync(backupPath, 1024, cancellationToken);
            if (Directory.Exists(destination))
            {
                // A complete previously staged version can be retried after interruption; never overwrite it.
                await VerifyExistingAsync(destination, payload, expected, cancellationToken);
            }
            else
            {
                staging = Resolve("FindIt/app/.update-stage-" + Guid.NewGuid().ToString("N"), false);
                Directory.CreateDirectory(staging);
                foreach (var item in expected.Values)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string relative = RelativeInsideVersion(item.Path, payload.Version);
                    string target = ResolveInside(staging, relative, false);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    EnsureNoLinks(target);
                    await ExtractVerifiedAsync(entries[item.Path], target, item, cancellationToken);
                }
                await VerifyExistingAsync(staging, payload, expected, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                EnsureNoLinks(destination); EnsureNoLinks(staging);
                Directory.Move(staging, destination); staging = null;
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Nothing below modifies the running version. The pointer is the final activation step.
            pointerTouched = true;
            await WriteAtomicAsync(backupPath, originalCurrent, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await WriteAtomicAsync(pointerPath, StrictUtf8.GetBytes(payload.Version), cancellationToken);
            return new() { Installed = true, Version = payload.Version, Message = $"FindIt {payload.Version} is verified and ready. Restart FindIt to use it. The previous version remains on the drive." };
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            string? rollbackError = null;
            if (pointerTouched && originalCurrent is not null)
            {
                try
                {
                    await RestoreIfDifferentAsync(Resolve("FindIt/app/current.txt", false), originalCurrent);
                    if (backupExisted) await RestoreIfDifferentAsync(Resolve("FindIt/app/current.previous.txt", false), originalBackup!);
                    else
                    {
                        string backup = Resolve("FindIt/app/current.previous.txt", false);
                        if (File.Exists(backup)) File.Delete(backup);
                    }
                }
                catch (Exception rollback) when (rollback is not OutOfMemoryException) { rollbackError = SafeMessage(rollback); }
            }
            if (error is OperationCanceledException && rollbackError is null) throw;
            return new() { Message = rollbackError is null ? "The update was not activated. Your current version is unchanged. " + SafeMessage(error) : "The update failed and its activation pointer could not be restored: " + rollbackError + ". The previous app folder is preserved." };
        }
        finally
        {
            updateLock?.Dispose();
            if (staging is not null) DeleteOwnedStaging(staging);
        }
    }

    private async Task<PublisherConfiguration?> ReadConfigurationAsync(CancellationToken cancellationToken)
    {
        var path = Resolve("FindIt/update-source.json", false);
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<PublisherConfiguration>(await ReadSmallFileAsync(path, 65536, cancellationToken), Json)
            ?? throw new InvalidDataException("The publisher update configuration is empty.");
    }

    private async Task<string> ReadCurrentAsync(CancellationToken cancellationToken)
    {
        string text = StrictUtf8.GetString(await ReadSmallFileAsync(Resolve("FindIt/app/current.txt", true), 1024, cancellationToken)).Trim();
        ValidateVersion(text); return text;
    }

    private static UpdatePayload VerifyEnvelope(byte[] bytes, string publicKeyPem)
    {
        var envelope = JsonSerializer.Deserialize<SignedEnvelope>(bytes, Json) ?? throw new InvalidDataException("The update envelope is empty.");
        if (envelope.Payload.Length > MaximumManifestBytes || envelope.Signature.Length > 2048) throw new InvalidDataException("The signed envelope is too large.");
        byte[] payload = Convert.FromBase64String(envelope.Payload), signature = Convert.FromBase64String(envelope.Signature);
        using var key = ImportPublicKey(publicKeyPem);
        if (!key.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)) throw new CryptographicException("The update signature does not match the trusted publisher key.");
        var manifest = JsonSerializer.Deserialize<UpdatePayload>(StrictUtf8.GetString(payload), Json) ?? throw new InvalidDataException("The signed payload is empty.");
        ValidatePayload(manifest); return manifest;
    }

    private static ECDsa ImportPublicKey(string pem)
    {
        if (string.IsNullOrWhiteSpace(pem) || !pem.Contains("-----BEGIN PUBLIC KEY-----", StringComparison.Ordinal) || pem.Contains("PRIVATE KEY", StringComparison.Ordinal)) throw new CryptographicException("A publisher ECDSA public key is required; private keys must never be distributed.");
        var key = ECDsa.Create();
        try { key.ImportFromPem(pem); if (key.KeySize is < 256 or > 521) throw new CryptographicException("The signing key must use a supported ECDSA curve of at least 256 bits."); return key; }
        catch { key.Dispose(); throw; }
    }

    private static Dictionary<string, UpdateFile> ValidatePayload(UpdatePayload payload)
    {
        ValidateVersion(payload.Version);
        if (payload.Files is null || payload.Files.Count is 0 or > MaximumFiles) throw new InvalidDataException("The signed payload has an invalid file count.");
        if (payload.DownloadUrl is not null && !TryHttps(payload.DownloadUrl, out _)) throw new InvalidDataException("Publisher download URLs must use HTTPS.");
        var expected = new Dictionary<string, UpdateFile>(StringComparer.OrdinalIgnoreCase); long total = 0;
        foreach (var file in payload.Files)
        {
            if (file is null) throw new InvalidDataException("The signed file list contains an empty entry.");
            string path = NormalizeEntryPath(file.Path);
            if (path != file.Path || RelativeInsideVersion(path, payload.Version).Length == 0) throw new InvalidDataException("Update paths must use canonical paths inside the new app version.");
            if (file.Size is < 0 or > MaximumFileBytes || !Regex.IsMatch(file.Sha256 ?? "", "^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant)) throw new InvalidDataException("The signed file size or SHA-256 is invalid.");
            if (!expected.TryAdd(path, file)) throw new InvalidDataException("The signed file list contains duplicate paths.");
            total = checked(total + file.Size); if (total > MaximumPackageBytes) throw new InvalidDataException("The expanded application exceeds the 4 GiB safety limit.");
        }
        if (!expected.ContainsKey($"FindIt/app/{payload.Version}/FindIt.App.exe") || !expected.ContainsKey($"FindIt/app/{payload.Version}/FindIt.Worker.exe")) throw new InvalidDataException("An update must include both the application and worker executables.");
        if (!expected.TryGetValue($"FindIt/app/{payload.Version}/package.sha256", out var launcherManifest) || launcherManifest.Size is <= 0 or > 4 * 1024 * 1024) throw new InvalidDataException("An update must include the launcher's bounded package.sha256 integrity manifest.");
        return expected;
    }

    private static Dictionary<string, ZipArchiveEntry> ValidateEntries(ZipArchive zip, string version, Dictionary<string, UpdateFile> expected)
    {
        var files = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long expanded = 0;
        foreach (var entry in zip.Entries)
        {
            string name = NormalizeEntryPath(entry.FullName.TrimEnd('/'));
            if (!seen.Add(name)) throw new InvalidDataException("Duplicate ZIP entries are not allowed.");
            int type = (entry.ExternalAttributes >> 16) & 0xf000;
            if (type == 0xa000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 || type is not (0 or 0x8000 or 0x4000)) throw new InvalidDataException("ZIP links and special files are not allowed.");
            if (entry.FullName.EndsWith('/'))
            {
                _ = RelativeInsideVersion(name + "/directory-check", version);
                if (entry.Length != 0) throw new InvalidDataException("Invalid directory ZIP entry.");
                continue;
            }
            if (name == EnvelopeFileName) continue;
            _ = RelativeInsideVersion(name, version);
            if (!expected.TryGetValue(name, out var manifestFile) || manifestFile.Size != entry.Length) throw new InvalidDataException("A ZIP entry is absent from the signed file list or has the wrong size.");
            if (entry.Length > MaximumFileBytes || entry.Length > 1024 * 1024 && (entry.CompressedLength == 0 || entry.Length / Math.Max(1, entry.CompressedLength) > 1000)) throw new InvalidDataException("A ZIP entry exceeds the decompression safety limits.");
            expanded = checked(expanded + entry.Length); if (expanded > MaximumPackageBytes) throw new InvalidDataException("The expanded update is too large.");
            files.Add(name, entry);
        }
        if (files.Count != expected.Count) throw new InvalidDataException("The ZIP is missing signed payload files.");
        return files;
    }

    private static async Task ExtractVerifiedAsync(ZipArchiveEntry entry, string target, UpdateFile expected, CancellationToken cancellationToken)
    {
        await using var input = entry.Open();
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536]; long size = 0;
        while (true)
        {
            int read = await input.ReadAsync(buffer, cancellationToken); if (read == 0) break;
            size = checked(size + read); if (size > expected.Size) throw new InvalidDataException("A ZIP entry expanded beyond its signed size.");
            digest.AppendData(buffer, 0, read); await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        if (size != expected.Size || !Convert.ToHexString(digest.GetHashAndReset()).Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("An update file failed its signed SHA-256 checksum.");
        output.Flush(true);
    }

    private async Task VerifyExistingAsync(string directory, UpdatePayload payload, Dictionary<string, UpdateFile> expected, CancellationToken cancellationToken)
    {
        EnsureNoLinks(directory);
        var pending = new Stack<string>(); pending.Push(directory); int count = 0;
        while (pending.TryPop(out var folder))
        {
            EnsureNoLinks(folder);
            foreach (var child in Directory.EnumerateFileSystemEntries(folder))
            {
                cancellationToken.ThrowIfCancellationRequested(); EnsureNoLinks(child);
                if (Directory.Exists(child)) { pending.Push(child); continue; }
                string name = $"FindIt/app/{payload.Version}/" + Path.GetRelativePath(directory, child).Replace('\\', '/');
                if (!expected.TryGetValue(name, out var file) || new FileInfo(child).Length != file.Size) throw new InvalidDataException("The version folder contains an unexpected or incomplete file.");
                await using var stream = new FileStream(child, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (!Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("A staged application file failed verification.");
                count++;
            }
        }
        if (count != expected.Count) throw new InvalidDataException("The staged application is incomplete.");
        // The native launcher independently consumes this manifest on the next launch. A signed
        // folder is not activatable unless that second integrity contract covers the same files.
        string prefix = $"FindIt/app/{payload.Version}/";
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string launcherText = StrictUtf8.GetString(await ReadSmallFileAsync(ResolveInside(directory, "package.sha256", true), 4 * 1024 * 1024, cancellationToken));
        foreach (string line in launcherText.Split('\n'))
        {
            string entry = line.TrimEnd('\r'); if (entry.Length == 0) continue;
            if (entry.Length < 67 || entry[64] != ' ' || entry[65] != ' ' || !Regex.IsMatch(entry[..64], "^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant)) throw new InvalidDataException("The launcher integrity manifest contains an invalid checksum line.");
            string relative = NormalizeEntryPath(entry[66..]);
            if (relative.Contains("..", StringComparison.Ordinal) || relative.Equals("package.sha256", StringComparison.OrdinalIgnoreCase) || !covered.Add(relative) || !expected.TryGetValue(prefix + relative, out var signedFile) || !entry[..64].Equals(signedFile.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The launcher integrity manifest disagrees with the signed payload.");
        }
        if (covered.Count != expected.Count - 1) throw new InvalidDataException("The launcher integrity manifest does not cover the complete application.");
    }

    private string Resolve(string relative, bool mustExist) => ResolveInside(root, relative, mustExist);
    private string ResolveInside(string directory, string relative, bool mustExist)
    {
        string canonical = NormalizeEntryPath(relative);
        string path = Path.GetFullPath(Path.Combine(directory, canonical.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = Path.EndsInDirectorySeparator(directory) ? directory : directory + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("An update path leaves its permitted folder.");
        EnsureNoLinks(path);
        if (mustExist && !File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException("An application file is unavailable.", path);
        return path;
    }

    private void EnsureNoLinks(string path)
    {
        string current = Path.GetFullPath(path);
        string prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!current.Equals(root, StringComparison.OrdinalIgnoreCase) && !current.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The update target is outside the portable drive.");
        while (true)
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Updates cannot use linked paths or junctions.");
            if (current.Equals(root, StringComparison.OrdinalIgnoreCase)) break;
            current = Path.GetDirectoryName(current) ?? throw new InvalidDataException("Invalid update path.");
        }
    }

    private static string NormalizeEntryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || Path.IsPathRooted(path) || path.Contains(':') || path.Any(c => char.IsControl(c))) throw new InvalidDataException("An update contains an unsafe path.");
        string canonical = path.Replace('\\', '/');
        foreach (string part in canonical.Split('/'))
        {
            if (part.Length is 0 or > 255 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(['<','>','"','|','?','*']) >= 0) throw new InvalidDataException("An update contains an unsafe path component.");
            string device = part.Split('.')[0];
            if (Regex.IsMatch(device, "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) throw new InvalidDataException("Device names are not valid update paths.");
        }
        return canonical;
    }

    private static string RelativeInsideVersion(string path, string version)
    {
        string prefix = $"FindIt/app/{version}/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("An update may only contain its new immutable app version. Archive data, shared models and other application versions cannot be changed.");
        string relative = path[prefix.Length..]; NormalizeEntryPath(relative);
        if (relative.Contains("..", StringComparison.Ordinal)) throw new InvalidDataException("An application path is incompatible with the portable launcher.");
        return relative;
    }

    private static void ValidateVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version) || version.Length > 64 || !Regex.IsMatch(version, @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[A-Za-z0-9]+(?:[.-][A-Za-z0-9]+)*)?$", RegexOptions.CultureInvariant)) throw new InvalidDataException("Update versions must be bounded semantic versions such as 1.2.0 or 1.2.0-preview.1.");
    }

    private static int CompareVersions(string candidate, string current)
    {
        ValidateVersion(candidate); ValidateVersion(current);
        var left = candidate.Split('-', 2); var right = current.Split('-', 2);
        var a = left[0].Split('.'); var b = right[0].Split('.');
        for (int i = 0; i < 3; i++) { int compare = System.Numerics.BigInteger.Parse(a[i], System.Globalization.CultureInfo.InvariantCulture).CompareTo(System.Numerics.BigInteger.Parse(b[i], System.Globalization.CultureInfo.InvariantCulture)); if (compare != 0) return compare; }
        if (left.Length != right.Length) return left.Length == 1 ? 1 : -1;
        if (left.Length == 1) return 0;
        var pa = left[1].Split('.'); var pb = right[1].Split('.');
        for (int i = 0; i < Math.Min(pa.Length, pb.Length); i++)
        {
            bool na = System.Numerics.BigInteger.TryParse(pa[i], out var va), nb = System.Numerics.BigInteger.TryParse(pb[i], out var vb);
            int compare = na && nb ? va.CompareTo(vb) : na != nb ? na ? -1 : 1 : string.Compare(pa[i], pb[i], StringComparison.Ordinal);
            if (compare != 0) return compare;
        }
        return pa.Length.CompareTo(pb.Length);
    }

    private static bool TryHttps(string? url, out Uri uri) => Uri.TryCreate(url, UriKind.Absolute, out uri!) && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo);
    private static async Task<byte[]> ReadSmallFileAsync(string path, int limit, CancellationToken cancellationToken)
    {
        if (new FileInfo(path).Length > limit) throw new InvalidDataException("An update configuration file is too large.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        return await ReadBoundedAsync(stream, limit, cancellationToken);
    }
    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int limit, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream(); var buffer = new byte[65536];
        while (true) { int read = await stream.ReadAsync(buffer, cancellationToken); if (read == 0) break; if (output.Length > limit - read) throw new InvalidDataException("An update manifest exceeds its safety limit."); await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken); }
        return output.ToArray();
    }
    private async Task WriteAtomicAsync(string path, byte[] content, CancellationToken cancellationToken)
    {
        EnsureNoLinks(path); string temporary = path + ".new-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous)) { await stream.WriteAsync(content, cancellationToken); stream.Flush(true); }
            cancellationToken.ThrowIfCancellationRequested(); EnsureNoLinks(path); File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) { EnsureNoLinks(temporary); File.Delete(temporary); } }
    }
    private async Task RestoreIfDifferentAsync(string path, byte[] original)
    {
        if (File.Exists(path))
        {
            byte[] existing = await ReadSmallFileAsync(path, 1024, CancellationToken.None);
            if (original.AsSpan().SequenceEqual(existing)) return;
        }
        await WriteAtomicAsync(path, original, CancellationToken.None);
    }
    private void DeleteOwnedStaging(string path)
    {
        try
        {
            EnsureNoLinks(path);
            string expected = appDirectory + Path.DirectorySeparatorChar + ".update-stage-";
            if (!Path.GetFullPath(path).StartsWith(expected, StringComparison.OrdinalIgnoreCase)) return;
            // Check every entry before recursively deleting this manager's fresh staging directory.
            var pending = new Stack<string>(); pending.Push(path);
            while (pending.TryPop(out var folder)) foreach (var entry in Directory.EnumerateFileSystemEntries(folder)) { EnsureNoLinks(entry); if (Directory.Exists(entry)) pending.Push(entry); }
            Directory.Delete(path, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { }
    }
    private static string SafeMessage(Exception error) => error.Message.Length > 500 ? error.Message[..500] : error.Message;
    private sealed record PublisherConfiguration { public string ManifestUrl { get; init; } = ""; public string PublicKeyPem { get; init; } = ""; }
    private sealed record SignedEnvelope { public string Payload { get; init; } = ""; public string Signature { get; init; } = ""; }
    private sealed record UpdatePayload { public string Version { get; init; } = ""; public IReadOnlyList<UpdateFile> Files { get; init; } = []; public string? DownloadUrl { get; init; } }
    private sealed record UpdateFile { public string Path { get; init; } = ""; public string Sha256 { get; init; } = ""; public long Size { get; init; } }
}
