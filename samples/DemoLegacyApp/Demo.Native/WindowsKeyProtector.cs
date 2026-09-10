using System.Security.Cryptography;

namespace Demo.Native;

/// <summary>Genera y firma con una clave RSA de CNG. DEPENDENCIA WINDOWS: RSACng (Cryptography Next
/// Generation) es específico de Windows; el equivalente portable es la fábrica RSA.Create().</summary>
public sealed class WindowsKeyProtector
{
    public byte[] Sign(byte[] data)
    {
        using var rsa = new RSACng(2048);
        return rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }
}
