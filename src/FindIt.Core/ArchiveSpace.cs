namespace FindIt.Core;

internal sealed class ArchiveSpaceException(string message):IOException(message);

internal static class ArchiveSpace
{
    private const long Reserve=256L*1024*1024;

    public static void EnsureAvailable(string root,long additionalNeeded=0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(additionalNeeded);
        PreparationFaults.Hit(root,"space.preflight");
        var drive=new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!);
        long available=drive.AvailableFreeSpace;
        if(available<Reserve||additionalNeeded>available-Reserve)
            throw new ArchiveSpaceException("Preparation stopped because this drive needs more free space. Free space on the drive, then resume; completed files and the last searchable snapshot are preserved.");
    }
}
