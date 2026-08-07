namespace FileHub.Tests;

public class TransferStatusTests
{
    [Theory]
    [InlineData(0, 200, 0)]
    [InlineData(50, 200, 25)]
    [InlineData(100, 200, 50)]
    [InlineData(200, 200, 100)]
    public void PercentComplete_ReturnsPercentageInZeroToHundredRange(long transferred, long total, double expected)
    {
        var status = new TransferStatus(transferred, total);

        Assert.Equal(expected, status.PercentComplete);
    }

    [Fact]
    public void PercentComplete_RoundsToTwoDecimalPlaces()
    {
        var status = new TransferStatus(1, 3);

        Assert.Equal(33.33, status.PercentComplete);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void PercentComplete_WhenTotalUnknown_ReturnsZero(long total)
    {
        var status = new TransferStatus(10, total);

        Assert.Equal(0, status.PercentComplete);
    }

    [Fact]
    public void FromPercent_ComputesBytesTransferredFromPercentage()
    {
        var status = TransferStatus.FromPercent(totalBytes: 200, percent: 50);

        Assert.Equal(100, status.BytesTransferred);
        Assert.Equal(200, status.TotalBytes);
        Assert.Equal(50, status.PercentComplete);
    }

    [Fact]
    public void FromPercent_HundredPercent_RoundTripsToHundred()
    {
        var status = TransferStatus.FromPercent(totalBytes: 1024, percent: 100);

        Assert.Equal(1024, status.BytesTransferred);
        Assert.Equal(100, status.PercentComplete);
    }
}
