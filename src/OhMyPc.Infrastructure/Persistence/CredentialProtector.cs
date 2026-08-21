using System.Security.Cryptography;
using System.Text;

namespace OhMyPc.Infrastructure.Persistence;

public sealed class CredentialProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("OhMyPc.Credentials.v1");

    public byte[] Protect(string value) => ProtectedData.Protect(
        Encoding.UTF8.GetBytes(value),
        Entropy,
        DataProtectionScope.CurrentUser);

    public string Unprotect(byte[] value) => Encoding.UTF8.GetString(ProtectedData.Unprotect(
        value,
        Entropy,
        DataProtectionScope.CurrentUser));
}
