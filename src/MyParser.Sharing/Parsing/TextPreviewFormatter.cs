namespace Shirobot.Plugin.MyParser.Parsing;

public static class TextPreviewFormatter
{
    public static string TrimLine(string value, int maxLength)
    {
        value = value.ReplaceLineEndings(" ").Trim();
        return value.Length <= maxLength ? value : value[..maxLength] + "…";
    }
}
