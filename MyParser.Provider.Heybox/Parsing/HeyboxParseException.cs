namespace MyParser.Provider.Heybox.Parsing;

public sealed class HeyboxParseException(string message, Exception? innerException = null) : Exception(message, innerException), IProviderClassifiedException
{
    public ProviderFailureKind FailureKind => ProviderFailureKind.Parse;
}
