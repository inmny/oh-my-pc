using System.Security.Cryptography;
using System.Text;
using OhMyPc.Infrastructure.Dsh;

namespace OhMyPc.IntegrationTests;

public sealed class KnownHostsTests
{
    private const string KeyBlob = "AAAAC3NzaC1lZDI1NTE5AAAAIB5fgv5P6pUwZLPhVr+kDyYRcFnBqDj9LMXNdyq";

    [Fact]
    public void GetKeyBlobs_MatchesPlainHostOnDefaultPort()
    {
        var content = $"github.com ssh-ed25519 {KeyBlob}\n" +
                      "example.com ssh-rsa AAAAOTHER\n";

        var blobs = KnownHosts.GetKeyBlobs(content, "github.com", 22);

        var blob = Assert.Single(blobs);
        Assert.Equal(KeyBlob, blob);
    }

    [Fact]
    public void GetKeyBlobs_MatchesBracketedHostOnCustomPort()
    {
        var content = $"[45.125.45.164]:35091 ssh-ed25519 {KeyBlob}\n";

        var blobs = KnownHosts.GetKeyBlobs(content, "45.125.45.164", 35091);

        Assert.Equal(KeyBlob, Assert.Single(blobs));
    }

    [Fact]
    public void GetKeyBlobs_MatchesHashedEntryIncludingCustomPort()
    {
        var candidate = KnownHosts.NormalizeHost("175.24.165.111", 22022);
        var salt = RandomNumberGenerator.GetBytes(20);
        using var hmac = new HMACSHA1(salt);
        var hash = hmac.ComputeHash(Encoding.ASCII.GetBytes(candidate));
        var content = $"|1|{Convert.ToBase64String(salt)}|{Convert.ToBase64String(hash)}| ssh-ed25519 {KeyBlob}\n";

        var blobs = KnownHosts.GetKeyBlobs(content, "175.24.165.111", 22022);

        Assert.Equal(KeyBlob, Assert.Single(blobs));
        Assert.Empty(KnownHosts.GetKeyBlobs(content, "175.24.165.111", 22));
        Assert.Empty(KnownHosts.GetKeyBlobs(content, "other.host", 22022));
    }

    [Fact]
    public void GetKeyBlobs_IgnoresCommentsAndBrokenLines()
    {
        var content = "# comment line\n" +
                      "\n" +
                      "only-one-token\n" +
                      $"plain.example,plain2.example ssh-ed25519 {KeyBlob}\n";

        Assert.Equal(KeyBlob, Assert.Single(KnownHosts.GetKeyBlobs(content, "plain2.example", 22)));
        Assert.Empty(KnownHosts.GetKeyBlobs(content, "missing.example", 22));
    }

    [Fact]
    public void Fingerprint_UsesSha256Base64WithoutPadding()
    {
        var fingerprint = KnownHosts.Fingerprint(new byte[] { 1, 2, 3, 4 });

        Assert.StartsWith("SHA256:", fingerprint);
        Assert.DoesNotContain("=", fingerprint);
        Assert.Equal(fingerprint, KnownHosts.Fingerprint(new byte[] { 1, 2, 3, 4 }));
        Assert.NotEqual(fingerprint, KnownHosts.Fingerprint(new byte[] { 1, 2, 3, 5 }));
    }
}
