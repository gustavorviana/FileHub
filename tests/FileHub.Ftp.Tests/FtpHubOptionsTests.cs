using FluentFTP;
using System.Net;
using System.Net.Security;

namespace FileHub.Ftp.Tests;

/// <summary>
/// Unit tests for the <see cref="FtpHubOptions"/> surface and the argument
/// validation in <see cref="FtpFileHub.Create(FtpHubOptions)"/>. The actual
/// TLS handshake is a passthrough to FluentFTP and is exercised only against a
/// live server (integration), not here.
/// </summary>
public class FtpHubOptionsTests
{
    [Fact]
    public void FromCredentials_SetsAllFields()
    {
        RemoteCertificateValidationCallback validate = (_, _, _, _) => true;

        var o = FtpHubOptions.FromCredentials(
            "ftp.example.com", port: 2121, user: "svc", password: "s3cret",
            rootPath: "/uploads", encryption: FtpEncryptionMode.Explicit,
            certificateValidation: validate);

        Assert.Equal("ftp.example.com", o.Host);
        Assert.Equal(2121, o.Port);
        Assert.Equal("svc", o.User);
        Assert.Equal("s3cret", o.Password);
        Assert.Equal("/uploads", o.RootPath);
        Assert.Equal(FtpEncryptionMode.Explicit, o.Encryption);
        Assert.True(o.DataConnectionEncryption); // default
        Assert.Same(validate, o.CertificateValidation);
        Assert.Null(o.Client);
    }

    [Fact]
    public void FromCredentials_Defaults_ArePlainFtp()
    {
        var o = FtpHubOptions.FromCredentials("host");

        Assert.Equal(21, o.Port);
        Assert.Equal("anonymous", o.User);
        Assert.Equal(FtpEncryptionMode.None, o.Encryption);
        Assert.Null(o.CertificateValidation);
    }

    [Fact]
    public void FromCredentials_NetworkCredential_MapsUserAndPassword()
    {
        var o = FtpHubOptions.FromCredentials(
            "host", 21, new NetworkCredential("bob", "pw"),
            encryption: FtpEncryptionMode.Implicit);

        Assert.Equal("bob", o.User);
        Assert.Equal("pw", o.Password);
        Assert.Equal(FtpEncryptionMode.Implicit, o.Encryption);
    }

    [Fact]
    public void Create_NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => FtpFileHub.Create(null!));
    }

    [Fact]
    public void Create_NoHostAndNoClient_ThrowsArgument()
    {
        var o = new FtpHubOptions { Host = "", RootPath = "/" };
        Assert.Throws<ArgumentException>(() => FtpFileHub.Create(o));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(70000)]
    public void Create_BadPort_ThrowsOutOfRange(int port)
    {
        var o = FtpHubOptions.FromCredentials("host", port: port);
        Assert.Throws<ArgumentOutOfRangeException>(() => FtpFileHub.Create(o));
    }
}
