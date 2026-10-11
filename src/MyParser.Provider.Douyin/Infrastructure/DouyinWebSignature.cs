using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Web;

namespace MyParser.Provider.Douyin.Infrastructure;

internal static class DouyinWebSignature
{
    private const string ProtocolSalt = "A96D855A08C0A9707F8BEF0D9A527E4E";

    // These detail endpoints require the final secsdk signature in addition to a_bogus.
    internal static string Apply(string url, DouyinGuestSession session, long? timestamp = null)
    {
        var uri = new Uri(url);
        if (uri.AbsolutePath is not ("/aweme/v1/web/aweme/detail/" or "/aweme/v1/web/aweme/post/")) return url;
        var visitor = session.Get("uifid") ?? session.Get("uifid_temp") ?? session.Get("uifidtemp");
        if (string.IsNullOrWhiteSpace(visitor)) return url;

        var fields = new List<KeyValuePair<string, string>>();
        foreach (var field in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = field.IndexOf('=');
            var key = HttpUtility.UrlDecode(separator < 0 ? field : field[..separator]);
            if (key is "timestamp" or "x-secsdk-web-signature") continue;
            var value = separator < 0 ? string.Empty : HttpUtility.UrlDecode(field[(separator + 1)..]);
            fields.Add(new(key, value));
        }
        var existingVisitor = fields.FirstOrDefault(field => field.Key == "uifid");
        if (existingVisitor.Key is null) fields.Add(new("uifid", visitor));
        else visitor = existingVisitor.Value;
        var seconds = (timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToString(CultureInfo.InvariantCulture);
        fields.Add(new("timestamp", seconds));
        var query = string.Join('&', fields.Select(field => field.Key + "=" + EncodeValue(field.Value)));
        var bytes = Encoding.UTF8.GetBytes(string.Join('_', visitor, seconds, ProtocolSalt, query));
        var signature = Convert.ToHexStringLower(MD5.HashData(bytes));
        return uri.GetLeftPart(UriPartial.Path) + "?" + query + "&x-secsdk-web-signature=" + signature;
    }

    private static string EncodeValue(string value) => Uri.EscapeDataString(value)
        .Replace("%21", "!").Replace("%27", "'").Replace("%28", "(").Replace("%29", ")").Replace("%2A", "*");
}
