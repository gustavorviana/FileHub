namespace FileHub.Tests;

public class FileHubExceptionTests
{
    [Fact]
    public void Constructor_WithMessage_SetsMessage()
    {
        var ex = new FileHubException("boom");

        Assert.Equal("boom", ex.Message);
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public void Constructor_WithMessageAndInner_SetsBoth()
    {
        var inner = new InvalidOperationException("inner");
        var ex = new FileHubException("outer", inner);

        Assert.Equal("outer", ex.Message);
        Assert.Same(inner, ex.InnerException);
    }

    [Fact]
    public void FileHubException_InheritsFromIOException()
    {
        var ex = new FileHubException("x");
        Assert.IsAssignableFrom<IOException>(ex);
    }
}
