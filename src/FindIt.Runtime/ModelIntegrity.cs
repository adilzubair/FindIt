using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FindIt.Runtime;

internal static class ModelIntegrity
{
    private static readonly ConcurrentDictionary<string,(long Bytes,long Ticks)> Verified=new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string,(long Bytes,string Sha)> Expected=new()
    {
        ["ggml-silero-v6.2.0.bin"]=(885098,"2aa269b785eeb53a82983a20501ddf7c1d9c48e33ab63a41391ac6c9f7fb6987"),
        ["embeddinggemma-2-Q8_0.gguf"]=(309855456,"2188ac1deca4b77dffefd603c2776a9d76d9d74ec01841392982ebb840b09135"),
        ["mmproj-embeddinggemma-2-Q8_0.gguf"]=(554821024,"c4a8a52691ecef40618438928bdf9e68379b854e24166f292592353db0aab64f"),
        ["face_detection_yunet_2023mar.onnx"]=(232589,"8f2383e4dd3cfbb4553ea8718107fc0423210dc964f9f4280604804ed2552fa4"),
        ["face_recognition_sface_2021dec.onnx"]=(38696353,"0ba9fbfa01b5270c96627c4ef784da859931e02f04419c829e83484087c34e79"),
        ["ggml-small.bin"]=(487601967,"1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b"),
        ["eng.traineddata"]=(4113088,"7d4322bd2a7749724879683fc3912cb542f19906c83bcc1a52132556427170b2"),
        ["ara.traineddata"]=(1432056,"e3206d3dc87fd50c24a0fb9f01838615911d25168f4e64415244b67d2bb3e729")
    };
    // Hashing a model on a USB drive costs a full read of it (about 18 s for the text
    // model at 17 MB/s), so a successful hash is remembered on this PC for the exact file
    // identity: volume, file index, size, creation and write time. Any change re-hashes.
    internal static string? RecordDirectory{get;set;}=DefaultRecordDirectory();
    internal static int HashesComputed=>hashes;
    private static int hashes;
    internal static async Task VerifyAsync(string path,string model,CancellationToken token)
    {
        if(!Expected.TryGetValue(model,out var expected))throw new InvalidDataException("Unrecognized model artifact: "+model);
        await VerifyAsync(path,model,expected,token);
    }
    internal static async Task VerifyAsync(string path,string model,(long Bytes,string Sha) expected,CancellationToken token)
    {
        var info=new FileInfo(path);var identity=(info.Length,info.LastWriteTimeUtc.Ticks);
        if(identity.Length!=expected.Bytes)throw new InvalidDataException("Model size mismatch: "+model+". Restore the verified bundle.");
        if(Verified.TryGetValue(path,out var previous)&&previous==identity)return;
        await using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,1024*1024,FileOptions.Asynchronous|FileOptions.SequentialScan);
        var record=Record(stream.SafeFileHandle,expected);
        if(record is not null&&IsRecorded(record.Value)){Verified[path]=identity;return;}
        Interlocked.Increment(ref hashes);
        var digest=Convert.ToHexStringLower(await SHA256.HashDataAsync(stream,token));
        if(digest!=expected.Sha)throw new InvalidDataException("Model SHA-256 mismatch: "+model+". Restore the verified bundle.");
        Verified[path]=identity;
        if(record is not null)Remember(record.Value);
    }
    internal static void Verify(string path,string model)=>VerifyAsync(path,model,CancellationToken.None).GetAwaiter().GetResult();
    internal static void ForgetSession()=>Verified.Clear();

    private static string? DefaultRecordDirectory()
    {
        var configured=Environment.GetEnvironmentVariable("FINDIT_VERIFICATION_CACHE");
        if(!string.IsNullOrWhiteSpace(configured))return configured;
        var local=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrEmpty(local)?null:Path.Combine(local,"FindIt","verification");
    }
    // The identity is read from the open handle, which also blocks writers while hashing.
    private static (string File,string Identity)? Record(SafeFileHandle handle,(long Bytes,string Sha) expected)
    {
        if(RecordDirectory is not { } directory||!OperatingSystem.IsWindows()||!GetFileInformationByHandle(handle,out var file))return null;
        var identity=$"model-v1|{expected.Sha}|{file.VolumeSerialNumber:X8}|{file.FileIndexHigh:X8}{file.FileIndexLow:X8}|{((long)file.FileSizeHigh<<32)|file.FileSizeLow}|{file.CreationTime}|{file.LastWriteTime}";
        return (Path.Combine(directory,Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))+".verified"),identity);
    }
    private static bool IsRecorded((string File,string Identity) record)
    {
        try{return File.Exists(record.File)&&File.ReadAllText(record.File)==record.Identity;}
        catch(Exception error)when(error is IOException or UnauthorizedAccessException){return false;}
    }
    private static void Remember((string File,string Identity) record)
    {
        // Best effort: an unwritable profile only means the next process hashes again.
        var temporary=record.File+".new-"+Guid.NewGuid().ToString("N");
        try{Directory.CreateDirectory(Path.GetDirectoryName(record.File)!);File.WriteAllText(temporary,record.Identity);File.Move(temporary,record.File,true);}
        catch(Exception error)when(error is IOException or UnauthorizedAccessException){try{File.Delete(temporary);}catch(Exception cleanup)when(cleanup is IOException or UnauthorizedAccessException){}}
    }
    // BY_HANDLE_FILE_INFORMATION: each FILETIME is two DWORDs, so 64-bit fields need Pack=4.
    [StructLayout(LayoutKind.Sequential,Pack=4)]
    private struct FileInformation{public uint Attributes;public long CreationTime;public long LastAccessTime;public long LastWriteTime;public uint VolumeSerialNumber;public uint FileSizeHigh;public uint FileSizeLow;public uint NumberOfLinks;public uint FileIndexHigh;public uint FileIndexLow;}
    [DllImport("kernel32",SetLastError=true)] private static extern bool GetFileInformationByHandle(SafeFileHandle file,out FileInformation information);
}
