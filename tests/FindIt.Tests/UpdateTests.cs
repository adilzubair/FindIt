using System.IO.Compression;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FindIt.Client;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FindIt.Tests;

[TestClass]
public sealed class UpdateTests
{
    private string root = "";
    private ECDsa key = null!;
    private string Current => Path.Combine(root, "FindIt", "app", "current.txt");
    [TestInitialize]
    public void Initialize()
    {
        root = Path.Combine(Path.GetTempPath(), "FindIt-update-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "FindIt", "app", "1.0.0"));
        File.WriteAllText(Current, "1.0.0");
        File.WriteAllText(Path.Combine(root, "FindIt", "app", "1.0.0", "FindIt.App.exe"), "running-version");
        Directory.CreateDirectory(Path.Combine(root, "FindIt", "archive"));
        Directory.CreateDirectory(Path.Combine(root, "FindIt", "models"));
        File.WriteAllText(Path.Combine(root, "FindIt", "archive", "original.txt"), "archive-must-not-change");
        File.WriteAllText(Path.Combine(root, "FindIt", "models", "model.gguf"), "model-must-not-change");
        key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        WriteConfiguration();
    }
    [TestCleanup]
    public void Cleanup()
    {
        key.Dispose();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    [TestMethod]
    public async Task MissingPublisherConfigurationDoesNotMakeNetworkRequests()
    {
        File.Delete(Path.Combine(root, "FindIt", "update-source.json"));
        using var transport = new ManifestHandler([]);
        var manager = new PortableUpdateManager(root, transport);
        var result = await manager.CheckAsync();
        Assert.IsFalse(result.VersionAvailable); Assert.AreEqual(0, transport.Calls);
        StringAssert.Contains(result.Message, "No network request");
        var install = await manager.InstallOfflineAsync("not-present.zip");
        Assert.IsFalse(install.Installed); Assert.AreEqual("1.0.0", await File.ReadAllTextAsync(Current));
    }

    [TestMethod]
    public async Task ManualCheckVerifiesTheSignedPublisherEnvelope()
    {
        var files = DefaultFiles("1.1.0");
        using var transport = new ManifestHandler(Envelope("1.1.0", files));
        var manager = new PortableUpdateManager(root, transport);
        Assert.AreEqual(0, transport.Calls, "Construction must not check for updates.");
        var result = await manager.CheckAsync();
        Assert.IsTrue(result.VersionAvailable); Assert.AreEqual("1.1.0", result.Version); Assert.AreEqual(1, transport.Calls);
        Assert.AreEqual("https://publisher.invalid/findit-1.1.0.zip", result.DownloadUrl);
        Assert.AreEqual("1.0.0", await File.ReadAllTextAsync(Current));
    }

    [TestMethod]
    public async Task InsecureManifestUrlIsRejectedBeforeNetwork()
    {
        WriteConfiguration("http://publisher.invalid/manifest.json");
        using var transport = new ManifestHandler([]);
        var result = await new PortableUpdateManager(root, transport).CheckAsync();
        Assert.IsFalse(result.VersionAvailable); Assert.AreEqual(0, transport.Calls); StringAssert.Contains(result.Message, "HTTPS");
    }

    [TestMethod]
    public async Task TamperedPublisherManifestCannotAdvertiseAnUpdate()
    {
        using var wrongKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var transport = new ManifestHandler(Envelope("1.1.0", DefaultFiles("1.1.0"), wrongKey));
        var result = await new PortableUpdateManager(root, transport).CheckAsync();
        Assert.IsFalse(result.VersionAvailable); StringAssert.Contains(result.Message, "signature");
    }

    [TestMethod]
    public async Task ValidOfflineUpdateActivatesNextLaunchAndPreservesArchiveModelsAndOldVersion()
    {
        string zip = CreatePackage("1.1.0");
        var result = await new PortableUpdateManager(root).InstallOfflineAsync(zip);
        Assert.IsTrue(result.Installed, result.Message);
        Assert.AreEqual("1.1.0", await File.ReadAllTextAsync(Current));
        Assert.AreEqual("1.0.0", await File.ReadAllTextAsync(Path.Combine(root, "FindIt", "app", "current.previous.txt")));
        Assert.AreEqual("running-version", await File.ReadAllTextAsync(Path.Combine(root, "FindIt", "app", "1.0.0", "FindIt.App.exe")));
        Assert.AreEqual("new-app", await File.ReadAllTextAsync(Path.Combine(root, "FindIt", "app", "1.1.0", "FindIt.App.exe")));
        Assert.AreEqual("archive-must-not-change", await File.ReadAllTextAsync(Path.Combine(root, "FindIt", "archive", "original.txt")));
        Assert.AreEqual("model-must-not-change", await File.ReadAllTextAsync(Path.Combine(root, "FindIt", "models", "model.gguf")));
        StringAssert.Contains(result.Message, "Restart FindIt");
    }

    [TestMethod]
    public async Task TamperedPayloadAndWrongSigningKeyLeaveActivationUnchanged()
    {
        var badPayload = await new PortableUpdateManager(root).InstallOfflineAsync(CreatePackage("1.1.0", tamperFile: true));
        Assert.IsFalse(badPayload.Installed); StringAssert.Contains(badPayload.Message, "checksum");
        Assert.AreEqual("1.0.0", await File.ReadAllTextAsync(Current));
        using var wrong = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var wrongSignature = await new PortableUpdateManager(root).InstallOfflineAsync(CreatePackage("1.2.0", signingKey: wrong));
        Assert.IsFalse(wrongSignature.Installed); StringAssert.Contains(wrongSignature.Message, "signature");
        Assert.IsFalse(Directory.Exists(Path.Combine(root, "FindIt", "app", "1.2.0")));
    }

    [TestMethod]
    public async Task SignedTraversalColonAndArchivePathsAreRejected()
    {
        foreach (string path in new[] { "FindIt/app/1.1.0/../../archive/evil.txt", "FindIt/app/1.1.0/evil.txt:stream", "FindIt/archive/evil.txt", "FindIt/models/evil.gguf", "FindIt/app/1.0.0/evil.dll" })
        {
            var files = DefaultFiles("1.1.0"); files[path] = Encoding.UTF8.GetBytes("evil");
            var result = await new PortableUpdateManager(root).InstallOfflineAsync(CreatePackage("1.1.0", files));
            Assert.IsFalse(result.Installed, path); Assert.AreEqual("1.0.0", await File.ReadAllTextAsync(Current));
        }
    }

    [TestMethod]
    public async Task UnsignedExtraEntriesAndSymlinkEntriesAreRejected()
    {
        var extra = await new PortableUpdateManager(root).InstallOfflineAsync(CreatePackage("1.1.0", extraEntry: "FindIt/archive/evil.txt"));
        Assert.IsFalse(extra.Installed);
        var link = await new PortableUpdateManager(root).InstallOfflineAsync(CreatePackage("1.1.0", symlink: true));
        Assert.IsFalse(link.Installed); StringAssert.Contains(link.Message, "links");
        Assert.AreEqual("1.0.0", await File.ReadAllTextAsync(Current));
    }

    [TestMethod]
    public async Task ExcessiveCompressionRatioIsRejectedBeforeExtraction()
    {
        var files = DefaultFiles("1.1.0"); files["FindIt/app/1.1.0/zeros.dll"] = new byte[4 * 1024 * 1024];
        var result = await new PortableUpdateManager(root).InstallOfflineAsync(CreatePackage("1.1.0", files, compression: CompressionLevel.SmallestSize));
        Assert.IsFalse(result.Installed); StringAssert.Contains(result.Message, "decompression");
        Assert.AreEqual("1.0.0", await File.ReadAllTextAsync(Current));
    }

    [TestMethod]
    public async Task PublisherScriptProducesAnAcceptedSignedPackageWithoutBundlingItsKey()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "scripts", "New-OfflineUpdate.ps1"))) directory = directory.Parent;
        Assert.IsNotNull(directory, "The publisher script must be present in the repository.");
        string source = Path.Combine(root, "published-source"); Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "FindIt.App.exe"), "publisher-app");
        await File.WriteAllTextAsync(Path.Combine(source, "FindIt.Worker.exe"), "publisher-worker");
        string privateKey = Path.Combine(root, "publisher-private.pem"); await File.WriteAllTextAsync(privateKey, key.ExportPkcs8PrivateKeyPem());
        string outputZip = Path.Combine(root, "publisher-update.zip");
        var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-File", Path.Combine(directory.FullName, "scripts", "New-OfflineUpdate.ps1"), "-Version", "1.1.0", "-AppDirectory", source, "-PrivateKeyPath", privateKey, "-OutputZip", outputZip, "-DownloadUrl", "https://publisher.invalid/findit-1.1.0.zip" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not launch the publisher script.");
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45)); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        Assert.AreEqual(0, process.ExitCode, (await output) + (await errors));
        Assert.IsFalse((await output).Contains("BEGIN PRIVATE KEY", StringComparison.Ordinal));
        Assert.IsTrue(File.Exists(outputZip + ".manifest.json"));
        using (var zip = ZipFile.OpenRead(outputZip)) Assert.IsFalse(zip.Entries.Any(e => e.FullName.EndsWith(".pem", StringComparison.OrdinalIgnoreCase)));
        var installed = await new PortableUpdateManager(root).InstallOfflineAsync(outputZip);
        Assert.IsTrue(installed.Installed, installed.Message);
        Assert.AreEqual("publisher-app", await File.ReadAllTextAsync(Path.Combine(root, "FindIt", "app", "1.1.0", "FindIt.App.exe")));
    }

    [TestMethod]
    public async Task SignedButStaleLauncherManifestCannotActivateAnUnlaunchableVersion()
    {
        var files = DefaultFiles("1.1.0");
        files["FindIt/app/1.1.0/package.sha256"] = Encoding.UTF8.GetBytes(new string('0', 64) + "  FindIt.App.exe\n");
        var result = await new PortableUpdateManager(root).InstallOfflineAsync(CreatePackage("1.1.0", files));
        Assert.IsFalse(result.Installed); StringAssert.Contains(result.Message, "launcher integrity manifest");
        Assert.AreEqual("1.0.0", await File.ReadAllTextAsync(Current));
    }

    [TestMethod]
    public async Task PreCanceledInstallLeavesTheActiveVersionUntouched()
    {
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => new PortableUpdateManager(root).InstallOfflineAsync(CreatePackage("1.1.0"), canceled.Token));
        Assert.AreEqual("1.0.0", await File.ReadAllTextAsync(Current));
        Assert.IsFalse(Directory.Exists(Path.Combine(root, "FindIt", "app", "1.1.0")));
    }

    [TestMethod]
    public async Task CancellationDuringExtractionDoesNotSwitchThePointer()
    {
        var files = DefaultFiles("1.1.0"); files["FindIt/app/1.1.0/large.dll"] = RandomNumberGenerator.GetBytes(32 * 1024 * 1024);
        string package = CreatePackage("1.1.0", files);
        using var cancellation = new CancellationTokenSource();
        var install = new PortableUpdateManager(root).InstallOfflineAsync(package, cancellation.Token);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!Directory.EnumerateDirectories(Path.Combine(root, "FindIt", "app"), ".update-stage-*").Any() && !install.IsCompleted && DateTime.UtcNow < deadline) await Task.Delay(1);
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await install);
        Assert.AreEqual("1.0.0", await File.ReadAllTextAsync(Current));
        Assert.IsFalse(Directory.Exists(Path.Combine(root, "FindIt", "app", "1.1.0")));
    }

    [TestMethod]
    public async Task PointerWriteFailureRestoresTheBackupAndSupportsSafeRetry()
    {
        string backup = Path.Combine(root, "FindIt", "app", "current.previous.txt"); await File.WriteAllTextAsync(backup, "0.9.0");
        string zip = CreatePackage("1.1.0");
        using (var locked = new FileStream(Current, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failed = await new PortableUpdateManager(root).InstallOfflineAsync(zip);
            Assert.IsFalse(failed.Installed); Assert.AreEqual("1.0.0", await File.ReadAllTextAsync(Current));
            Assert.AreEqual("0.9.0", await File.ReadAllTextAsync(backup));
        }
        var retry = await new PortableUpdateManager(root).InstallOfflineAsync(zip);
        Assert.IsTrue(retry.Installed, retry.Message); Assert.AreEqual("1.1.0", await File.ReadAllTextAsync(Current));
    }

    [TestMethod]
    public async Task DowngradeAndUnexpectedPreexistingVersionAreRejected()
    {
        var old = await new PortableUpdateManager(root).InstallOfflineAsync(CreatePackage("0.9.0"));
        Assert.IsFalse(old.Installed); Assert.AreEqual("1.0.0", await File.ReadAllTextAsync(Current));
        string destination = Path.Combine(root, "FindIt", "app", "1.1.0"); Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(destination, "untrusted.dll"), "not-signed");
        var invalid = await new PortableUpdateManager(root).InstallOfflineAsync(CreatePackage("1.1.0"));
        Assert.IsFalse(invalid.Installed); Assert.AreEqual("not-signed", await File.ReadAllTextAsync(Path.Combine(destination, "untrusted.dll")));
        Assert.AreEqual("1.0.0", await File.ReadAllTextAsync(Current));
    }

    private void WriteConfiguration(string url = "https://publisher.invalid/manifest.json") => File.WriteAllText(Path.Combine(root, "FindIt", "update-source.json"), JsonSerializer.Serialize(new { manifestUrl = url, publicKeyPem = key.ExportSubjectPublicKeyInfoPem() }));
    private static Dictionary<string, byte[]> DefaultFiles(string version) => new(StringComparer.Ordinal)
    {
        [$"FindIt/app/{version}/FindIt.App.exe"] = Encoding.UTF8.GetBytes("new-app"),
        [$"FindIt/app/{version}/FindIt.Worker.exe"] = Encoding.UTF8.GetBytes("new-worker"),
        [$"FindIt/app/{version}/runtime/native/helper.dll"] = Encoding.UTF8.GetBytes("native-payload")
    };
    private byte[] Envelope(string version, Dictionary<string, byte[]> files, ECDsa? signingKey = null)
    {
        EnsureLauncherManifest(version, files);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new { version, files = files.Select(f => new { path = f.Key, sha256 = Convert.ToHexString(SHA256.HashData(f.Value)), size = f.Value.LongLength }).ToArray(), downloadUrl = $"https://publisher.invalid/findit-{version}.zip" });
        byte[] signature = (signingKey ?? key).SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return JsonSerializer.SerializeToUtf8Bytes(new { payload = Convert.ToBase64String(payload), signature = Convert.ToBase64String(signature) });
    }
    private static void EnsureLauncherManifest(string version, Dictionary<string, byte[]> files)
    {
        string prefix = $"FindIt/app/{version}/";
        string name = prefix + "package.sha256";
        if (files.ContainsKey(name)) return;
        files[name] = Encoding.UTF8.GetBytes(string.Join('\n', files.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => Convert.ToHexString(SHA256.HashData(f.Value)) + "  " + f.Key[prefix.Length..].Replace('/', '\\'))) + "\n");
    }
    private string CreatePackage(string version, Dictionary<string, byte[]>? files = null, ECDsa? signingKey = null, bool tamperFile = false, string? extraEntry = null, bool symlink = false, CompressionLevel compression = CompressionLevel.NoCompression)
    {
        files ??= DefaultFiles(version);
        string path = Path.Combine(root, "update-" + Guid.NewGuid().ToString("N") + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var manifest = zip.CreateEntry(PortableUpdateManager.EnvelopeFileName).Open()) manifest.Write(Envelope(version, files, signingKey));
        int number = 0;
        foreach (var file in files)
        {
            var entry = zip.CreateEntry(file.Key, compression);
            if (symlink && number == 0) entry.ExternalAttributes = unchecked((int)0xa1ff0000);
            using var output = entry.Open();
            byte[] bytes = tamperFile && number == 0 ? Enumerable.Repeat((byte)'x', file.Value.Length).ToArray() : file.Value;
            output.Write(bytes); number++;
        }
        if (extraEntry is not null) using (var output = zip.CreateEntry(extraEntry).Open()) output.WriteByte(1);
        return path;
    }
    private sealed class ManifestHandler(byte[] manifest) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; Assert.AreEqual("https", request.RequestUri!.Scheme);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(manifest) });
        }
    }
}
