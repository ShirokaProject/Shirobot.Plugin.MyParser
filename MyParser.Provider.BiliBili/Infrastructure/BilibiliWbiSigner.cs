using System.Security.Cryptography;
using System.Text;
using System.Web;
using MyParser.Provider.BiliBili.Models;

namespace MyParser.Provider.BiliBili.Infrastructure;

public static class BilibiliWbiSigner
{
    private static readonly int[] MixinKeyEncTab =
    [
        46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35,
        27, 43, 5, 49, 33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13,
        37, 48, 7, 16, 24, 55, 40, 61, 26, 17, 0, 1, 60, 51, 30, 4,
        22, 25, 54, 21, 56, 59, 6, 63, 57, 62, 11, 36, 20, 34, 44, 52,
    ];

    public static string CreateMixinKey(string imgUrl, string subUrl)
    {
        var imgKey = ExtractKey(imgUrl);
        var subKey = ExtractKey(subUrl);
        var raw = imgKey + subKey;
        if (raw.Length < 64)
        {
            throw new BilibiliParseException("获取 WBI key 失败");
        }

        return string.Concat(MixinKeyEncTab.Select(i => raw[i]))[..32];
    }

    public static Dictionary<string, string> Sign(IReadOnlyDictionary<string, object?> parameters, string mixinKey)
    {
        var signed = parameters
            .Where(i => i.Value is not null)
            .ToDictionary(i => i.Key, i => i.Value!.ToString() ?? string.Empty, StringComparer.Ordinal);
        signed["wts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

        var filtered = signed
            .OrderBy(i => i.Key, StringComparer.Ordinal)
            .ToDictionary(i => i.Key, i => new string(i.Value.Where(ch => !"!'()*".Contains(ch)).ToArray()), StringComparer.Ordinal);
        var query = string.Join("&", filtered.Select(i => $"{HttpUtility.UrlEncode(i.Key)}={HttpUtility.UrlEncode(i.Value)}"));
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(query + mixinKey));
        filtered["w_rid"] = Convert.ToHexString(hash).ToLowerInvariant();
        return filtered;
    }

    private static string ExtractKey(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
        var name = path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
        var dot = name.IndexOf('.');
        return dot >= 0 ? name[..dot] : name;
    }
}
