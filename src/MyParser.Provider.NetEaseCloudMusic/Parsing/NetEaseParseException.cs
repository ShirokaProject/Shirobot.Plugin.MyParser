namespace MyParser.Provider.NetEaseCloudMusic.Parsing;

public sealed class NetEaseParseException(string message) : Exception(message), IProviderClassifiedException
{
    public ProviderFailureKind FailureKind => ProviderFailureKind.Parse;
}
