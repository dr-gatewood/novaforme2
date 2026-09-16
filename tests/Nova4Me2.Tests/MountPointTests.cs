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
}
