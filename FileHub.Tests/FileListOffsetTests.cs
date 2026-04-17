namespace FileHub.Tests;

public class FileListOffsetTests
{
    [Fact]
    public void Default_IsIndexZero_NotNamed()
    {
        FileListOffset offset = default;

        Assert.Equal(0, offset.Index);
        Assert.Null(offset.Name);
        Assert.False(offset.IsNamed);
    }

    [Fact]
    public void ImplicitFromInt_ProducesIndexOffset()
    {
        FileListOffset offset = 5;

        Assert.Equal(5, offset.Index);
        Assert.False(offset.IsNamed);
    }

    [Fact]
    public void FromIndex_Negative_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FileListOffset.FromIndex(-1));
    }

    [Fact]
    public void ImplicitFromInt_Negative_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => { FileListOffset _ = -1; });
    }

    [Fact]
    public void FromName_ProducesNamedOffset()
    {
        var offset = FileListOffset.FromName("file.txt");

        Assert.Equal("file.txt", offset.Name);
        Assert.True(offset.IsNamed);
    }

    [Fact]
    public void FromName_NullOrEmpty_Throws()
    {
        Assert.Throws<ArgumentException>(() => FileListOffset.FromName(null));
        Assert.Throws<ArgumentException>(() => FileListOffset.FromName(""));
    }

    [Fact]
    public void Equality_MatchesByIndexOrName()
    {
        Assert.Equal(FileListOffset.FromIndex(3), FileListOffset.FromIndex(3));
        Assert.Equal(FileListOffset.FromName("a"), FileListOffset.FromName("a"));
        Assert.NotEqual(FileListOffset.FromIndex(3), FileListOffset.FromName("a"));
    }
}
