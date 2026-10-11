using System.Runtime;

namespace Compositor.App.Services;

/// <summary>
/// Gives memory back after big work (a closed fill workspace, a finished removal): .NET keeps freed blocks for reuse,
/// and the large ones (whole pictures) are only returned to Windows by a compacting collection.
/// </summary>
public static class MemoryRelease
{
    public static void Compact()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    }
}