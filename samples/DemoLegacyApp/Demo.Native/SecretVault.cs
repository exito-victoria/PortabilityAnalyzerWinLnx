using System.Security.Cryptography;
using System.Text;

namespace Demo.Native;

/// <summary>Protege un secreto en reposo con DPAPI. DEPENDENCIA WINDOWS: ProtectedData/DataProtectionScope
/// (DPAPI) es exclusivo de Windows y lanza PlatformNotSupportedException fuera de Windows; el equivalente
/// multiplataforma es ASP.NET Core Data Protection (Microsoft.AspNetCore.DataProtection).</summary>
public sealed class SecretVault
{
    public byte[] Protect(string secret) =>
        ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser);

    public string Unprotect(byte[] blob) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(blob, null, DataProtectionScope.CurrentUser));
}
