using System.Security.Cryptography;
using System.Text;

namespace EmbyPlayer.Core.Authentication;

public static class AuthSessionCredentialTarget
{
    private const string TargetPrefix = "EmbyPlayer.AuthSession";

    public static string Create(string serverBase, string userId)
    {
        if (string.IsNullOrWhiteSpace(serverBase))
        {
            throw new ArgumentException("Server base is required.", nameof(serverBase));
        }

        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException("User id is required.", nameof(userId));
        }

        var normalizedServerBase = serverBase.Trim().TrimEnd('/');
        var serverHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalizedServerBase)))
            .ToLowerInvariant();

        return $"{TargetPrefix}.{serverHash}.{userId}";
    }
}
