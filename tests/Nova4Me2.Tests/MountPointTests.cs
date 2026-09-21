using Nova4Me2.Core.Ntfs;
using Nova4Me2.Mount;
using Xunit;

namespace Nova4Me2.Tests;

public class MountPointTests
{
    [Theory]
    [InlineData("r", "R:")]
    [InlineData("R:", "R:")]
    [InlineData(@"r:\", "R:")]
    [InlineData(@"\\.\R:", "R:")]
    [InlineData(@"C:\mnt\apps\", @"C:\mnt\apps")]
    public void NormalizeMountPoint_HandlesLettersAndFolders(string input, string expected) => Assert.Equal(expected, MountSession.NormalizeMountPoint(input));

    [Fact]
    public void Candidates_TriesMountManagerDriveBeforePlainLetter()
    {
        var c = MountSession.Candidates("r:");
        Assert.Equal(new[] { @"\\.\R:", "R:" }, c);
        Assert.Equal(new[] { @"C:\mnt\apps" }, MountSession.Candidates(@"C:\mnt\apps\"));
    }

    [Theory]
    [InlineData(NtfsFileAttributes.Archive, false, 0x20u)]
    [InlineData(NtfsFileAttributes.ReadOnly | NtfsFileAttributes.Archive, false, 0x21u)]
    [InlineData((NtfsFileAttributes)0, false, 0x80u)]
    [InlineData(NtfsFileAttributes.Directory, true, 0x10u)]
    [InlineData(NtfsFileAttributes.Hidden | NtfsFileAttributes.ReparsePoint, false, 0x02u)]
    public void MapAttributes_DoesNotStampReadOnlyOnEveryFile(NtfsFileAttributes ntfs, bool dir, uint expected)
        => Assert.Equal(expected, NtfsReadOnlyFileSystem.MapAttributes(ntfs, dir));
}
