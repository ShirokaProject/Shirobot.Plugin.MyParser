using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MyParser.Provider.NetEaseCloudMusic.Infrastructure;

internal static class NetEaseCrypto
{
    private static readonly byte[] AesKey = "e82ckenh8dichen8"u8.ToArray();

    public static string EncryptEApiParams(string url, object payload)
    {
        var path = new Uri(url).AbsolutePath.Replace("/eapi/", "/api/", StringComparison.Ordinal);
        var json = JsonSerializer.Serialize(payload, NetEaseJson.Options);
        var digest = Md5Hex($"nobody{path}use{json}md5forencrypt");
        var text = $"{path}-36cd479b6b5-{json}-36cd479b6b5-{digest}";
        using var aes = Aes.Create();
        aes.Key = AesKey;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;
        using var encryptor = aes.CreateEncryptor();
        var bytes = Encoding.UTF8.GetBytes(text);
        return Convert.ToHexString(encryptor.TransformFinalBlock(bytes, 0, bytes.Length)).ToLowerInvariant();
    }

    private static string Md5Hex(string text)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
