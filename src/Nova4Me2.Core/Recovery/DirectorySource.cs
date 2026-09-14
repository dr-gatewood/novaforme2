using Nova4Me2.Core.Ntfs;

namespace Nova4Me2.Core.Recovery;

/// <summary>Abstracts "how do I list a directory": through the live $I30 index, or from a rebuilt MFT scan.</summary>
public interface IDirectorySource
{
    NtfsVolume Volume { get; }
    string Name { get; }
    List<NtfsEntry> List(NtfsEntry directory);
    NtfsEntry Root { get; }
}

public sealed class IndexDirectorySource(NtfsVolume vol) : IDirectorySource
{
    public NtfsVolume Volume => vol;
    public string Name => "Directory index ($I30)";
    public NtfsEntry Root => vol.RootEntry;
    public List<NtfsEntry> List(NtfsEntry directory) => vol.ListDirectory(directory.Record, directory.Path);
}

public sealed class MftIndexDirectorySource(NtfsVolume vol, MftIndex index) : IDirectorySource
{
    public NtfsVolume Volume => vol;
    public MftIndex Index => index;
    public string Name => "Rebuilt from $MFT scan";
    public NtfsEntry Root => vol.RootEntry;

    public List<NtfsEntry> List(NtfsEntry directory)
    {
        var l = index.ListChildren(directory.Record);
        if (directory.Record == NtfsVolume.RootRecord && index.Orphans.Count > 0)
        {
            l = new List<NtfsEntry>(l)
            {
                new NtfsEntry { Record = MftIndex.OrphanRecord, Parent = NtfsVolume.RootRecord, Name = "[Orphaned files]", IsDirectory = true, Attributes = NtfsFileAttributes.Directory, IsOrphan = true, Path = "[Orphaned files]" }
            };
        }
        return l;
    }
}
