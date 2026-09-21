using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace XbotDeployer.Core;

public static class CryptoHelper
{
    private const string DefaultRsaPublicKeyB64 =
        "MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQCte0XfPY9GUpQ3ZasH1kVbDhRwyRAq" +
        "WSeyxj290OqFHtyiZ+5SQjrEr79mk0hcZqV03fb5oYf385E3gopSERIKxVQyGoloNeDg" +
        "yLu7rHHWMPo8KPDpUBlpRpHlGMgBNzJZ2BI6p7LvGAhCoA7XRuetyTlAW6EbSXBpSu1s" +
        "NGBhkQIDAQAB";

    private static readonly byte[] AesFlowKey = Convert.FromBase64String("pO22DRcoQiho/omL8plzGQ==");
    private static readonly byte[] AesFlowIv = Encoding.UTF8.GetBytes("keosmnvbhdueyr2b");

    public static string EncryptPassword(string password, string pubKeyB64 = DefaultRsaPublicKeyB64)
    {
        var keyDer = Convert.FromBase64String(pubKeyB64);
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(keyDer, out _);
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        var encryptedBytes = rsa.Encrypt(passwordBytes, RSAEncryptionPadding.Pkcs1);
        return Convert.ToBase64String(encryptedBytes);
    }

    public static (JsonDocument? Doc, bool WasEncrypted) DecryptFlowData(byte[] rawData)
    {
        var text = Encoding.UTF8.GetString(rawData).Trim();
        if (text.StartsWith('{'))
        {
            try
            {
                var doc = JsonDocument.Parse(text);
                return (doc, false);
            }
            catch
            {
                return (null, false);
            }
        }

        try
        {
            var ciphertext = Convert.FromBase64String(text);
            using var aes = Aes.Create();
            aes.Key = AesFlowKey;
            aes.IV = AesFlowIv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var decryptor = aes.CreateDecryptor();
            var decrypted = decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
            var doc = JsonDocument.Parse(decrypted);
            return (doc, true);
        }
        catch
        {
            return (null, false);
        }
    }

    public static byte[] EncryptFlowData(string jsonString)
    {
        var rawBytes = Encoding.UTF8.GetBytes(jsonString);
        using var aes = Aes.Create();
        aes.Key = AesFlowKey;
        aes.IV = AesFlowIv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var encryptor = aes.CreateEncryptor();
        var ciphertext = encryptor.TransformFinalBlock(rawBytes, 0, rawBytes.Length);
        var b64 = Convert.ToBase64String(ciphertext);
        return Encoding.UTF8.GetBytes(b64);
    }
}
