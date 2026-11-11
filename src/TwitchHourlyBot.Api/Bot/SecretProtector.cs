using Microsoft.AspNetCore.DataProtection;

namespace TwitchHourlyBot.Api.Bot;

/// <summary>Encrypts secrets at rest with ASP.NET Core Data Protection.</summary>
public sealed class SecretProtector
{
    private readonly IDataProtector _protector;

    public SecretProtector(IDataProtectionProvider provider) =>
        _protector = provider.CreateProtector("TwitchHourlyBot.Secrets.v1");

    public string Protect(string plain) => string.IsNullOrEmpty(plain) ? "" : _protector.Protect(plain);

    /// <summary>Returns an empty string if the value is empty or cannot be decrypted (for example the key ring was lost).</summary>
    public string Unprotect(string protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue)) return "";
        try { return _protector.Unprotect(protectedValue); }
        catch (System.Security.Cryptography.CryptographicException) { return ""; }
    }
}
