using System;

namespace FileHub.Tests;

public class MultipartUploadSpecTests
{
    [Fact]
    public void FromPartSize_DivisibleTotal_ProducesExactPartCount()
    {
        var spec = MultipartUploadSpec.FromPartSize(totalBytes: 30, partSize: 10);

        Assert.Equal(30, spec.TotalBytes);
        Assert.Equal(10, spec.PartSize);
        Assert.Equal(3, spec.PartCount);
    }

    [Fact]
    public void FromPartSize_NonDivisibleTotal_AddsExtraPartForRemainder()
    {
        var spec = MultipartUploadSpec.FromPartSize(totalBytes: 25, partSize: 10);

        Assert.Equal(3, spec.PartCount);
        Assert.Equal(10, spec.GetPartLength(1));
        Assert.Equal(10, spec.GetPartLength(2));
        Assert.Equal(5, spec.GetPartLength(3));
    }

    [Fact]
    public void FromPartSize_SinglePart_WhenTotalFitsInOnePart()
    {
        var spec = MultipartUploadSpec.FromPartSize(totalBytes: 5, partSize: 10);

        Assert.Equal(1, spec.PartCount);
        Assert.Equal(5, spec.GetPartLength(1));
    }

    [Fact]
    public void FromPartCount_DivisibleTotal_ProducesEqualParts()
    {
        var spec = MultipartUploadSpec.FromPartCount(totalBytes: 1000, partCount: 4);

        Assert.Equal(250, spec.PartSize);
        Assert.Equal(4, spec.PartCount);
        for (int i = 1; i <= 4; i++)
            Assert.Equal(250, spec.GetPartLength(i));
    }

    [Fact]
    public void FromPartCount_NonDivisibleTotal_LastPartCarriesRemainder()
    {
        // ceil(1000 / 3) = 334; parts 1,2 = 334, part 3 = 332.
        var spec = MultipartUploadSpec.FromPartCount(totalBytes: 1000, partCount: 3);

        Assert.Equal(334, spec.PartSize);
        Assert.Equal(3, spec.PartCount);
        Assert.Equal(334, spec.GetPartLength(1));
        Assert.Equal(334, spec.GetPartLength(2));
        Assert.Equal(332, spec.GetPartLength(3));
        // Sum of parts equals total
        Assert.Equal(1000L, spec.GetPartLength(1) + spec.GetPartLength(2) + spec.GetPartLength(3));
    }

    [Fact]
    public void FromPartSize_BigScenario_1Gb_10Mb_Gives100Parts()
    {
        var spec = MultipartUploadSpec.FromPartSize(totalBytes: 1_000_000_000, partSize: 10_000_000);

        Assert.Equal(100, spec.PartCount);
        Assert.Equal(10_000_000, spec.GetPartLength(1));
        Assert.Equal(10_000_000, spec.GetPartLength(100));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void FromPartSize_NonPositiveTotal_Throws(long totalBytes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MultipartUploadSpec.FromPartSize(totalBytes, partSize: 10));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void FromPartSize_NonPositivePartSize_Throws(long partSize)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MultipartUploadSpec.FromPartSize(totalBytes: 100, partSize: partSize));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void FromPartCount_NonPositivePartCount_Throws(int partCount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MultipartUploadSpec.FromPartCount(totalBytes: 100, partCount: partCount));
    }

    [Fact]
    public void GetPartLength_OutOfRange_Throws()
    {
        var spec = MultipartUploadSpec.FromPartSize(totalBytes: 30, partSize: 10);

        Assert.Throws<ArgumentOutOfRangeException>(() => spec.GetPartLength(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => spec.GetPartLength(4));
    }

    [Fact]
    public void Equals_SameFields_True()
    {
        var a = MultipartUploadSpec.FromPartSize(100, 25);
        var b = MultipartUploadSpec.FromPartSize(100, 25);

        Assert.True(a.Equals(b));
        Assert.True(a == b);
        Assert.False(a != b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Equals_DifferentFields_False()
    {
        var a = MultipartUploadSpec.FromPartSize(100, 25);
        var c = MultipartUploadSpec.FromPartSize(100, 50);

        Assert.False(a.Equals(c));
        Assert.True(a != c);
    }

    [Fact]
    public void ToString_IncludesAllFields()
    {
        var spec = MultipartUploadSpec.FromPartSize(100, 25);
        var s = spec.ToString();

        Assert.Contains("TotalBytes=100", s);
        Assert.Contains("PartSize=25", s);
        Assert.Contains("PartCount=4", s);
    }
}
