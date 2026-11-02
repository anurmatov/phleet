using System.Runtime.InteropServices;
using Fleet.Agent;

namespace Fleet.Agent.Tests;

public sealed class StockNativePathProbeTests
{
    [Fact]
    public void StatxAbi_HasCompleteLinuxLayout()
    {
        Assert.Equal(256, Marshal.SizeOf<StockNativePathProbe.Statx>());
        Assert.Equal(0, Marshal.OffsetOf<StockNativePathProbe.Statx>(nameof(StockNativePathProbe.Statx.Mask)).ToInt32());
        Assert.Equal(28, Marshal.OffsetOf<StockNativePathProbe.Statx>(nameof(StockNativePathProbe.Statx.Mode)).ToInt32());
    }

    [Theory]
    [InlineData("file")]
    [InlineData("directory")]
    [InlineData("link")]
    [InlineData("dangling")]
    public void Probe_RealEntriesArePresentWithoutFollowingLinks(string kind)
    {
        var root = Directory.CreateTempSubdirectory("native-probe-").FullName;
        try
        {
            var path = Path.Combine(root, "entry");
            if (kind == "file") File.WriteAllText(path, "synthetic");
            else if (kind == "directory") Directory.CreateDirectory(path);
            else File.CreateSymbolicLink(path, kind == "link" ? root : Path.Combine(root, "absent"));
            var result = StockNativePathProbe.Probe(path);
            Assert.Equal(StockNativePathProbe.State.Present, result.Status);
            Assert.Equal(kind is "link" or "dangling" ? 0xa000 : kind == "directory" ? 0x4000 : 0x8000, result.Mode & 0xf000);
            Assert.Equal(StockNativePathProbe.State.Absent, StockNativePathProbe.Probe(Path.Combine(root, "absent")).Status);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(1)] [InlineData(5)] [InlineData(4)] [InlineData(13)]
    [InlineData(20)] [InlineData(38)] [InlineData(40)]
    public void Probe_ErrorsAreNotAbsence(int error)
    {
        var result = StockNativePathProbe.Probe("/synthetic", _ => new(-1, error, default));
        Assert.Equal(StockNativePathProbe.State.Unobservable, result.Status);
        Assert.Equal(error, result.Error);
    }

    [Fact]
    public void Probe_OnlyEnoentIsAbsenceAndMissingFieldsOrEntryPointRefuse()
    {
        Assert.Equal(StockNativePathProbe.State.Absent, StockNativePathProbe.Probe("/synthetic", _ => new(-1, 2, default)).Status);
        Assert.Equal(StockNativePathProbe.State.Unobservable, StockNativePathProbe.Probe("/synthetic", _ => new(0, 0, default)).Status);
        Assert.Equal(StockNativePathProbe.State.Unobservable, StockNativePathProbe.Probe("/synthetic", _ => throw new EntryPointNotFoundException()).Status);
        Assert.Equal(StockNativePathProbe.State.Unobservable, StockNativePathProbe.Probe("/synthetic", _ => new(1, 0, default)).Status);
    }
}
