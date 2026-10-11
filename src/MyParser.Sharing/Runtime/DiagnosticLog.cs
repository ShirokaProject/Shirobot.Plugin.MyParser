using System.Runtime.CompilerServices;
using ShiroBot.SDK.Abstractions;

namespace Shirobot.Plugin.MyParser;

/// <summary>Development diagnostics; disabled interpolations do not evaluate their payloads.</summary>
public static class DiagnosticLog
{
    private static Func<bool>? enabled;
    public static bool IsEnabled => Volatile.Read(ref enabled)?.Invoke() == true;
    public static void Configure(Func<bool>? isEnabled) => Volatile.Write(ref enabled, isEnabled);
    public static void Info(ref DiagnosticMessage message) { if (message.Enabled) BotLog.Info(message.GetText()); }
    public static void Info(string message) { if (IsEnabled) BotLog.Info(message); }
    public static void Warning(ref DiagnosticMessage message) { if (message.Enabled) BotLog.Warning(message.GetText()); }
    public static void Warning(string message) { if (IsEnabled) BotLog.Warning(message); }
}

[InterpolatedStringHandler]
public ref struct DiagnosticMessage
{
    private DefaultInterpolatedStringHandler builder;
    public bool Enabled { get; }
    public DiagnosticMessage(int literalLength, int formattedCount, out bool enabled)
    {
        Enabled = enabled = DiagnosticLog.IsEnabled;
        builder = enabled ? new DefaultInterpolatedStringHandler(literalLength, formattedCount) : default;
    }
    public void AppendLiteral(string value) => builder.AppendLiteral(value);
    public void AppendFormatted<T>(T value) => builder.AppendFormatted(value);
    public void AppendFormatted<T>(T value, string? format) => builder.AppendFormatted(value, format);
    public void AppendFormatted<T>(T value, int alignment) => builder.AppendFormatted(value, alignment);
    public void AppendFormatted<T>(T value, int alignment, string? format) => builder.AppendFormatted(value, alignment, format);
    public string GetText() => builder.ToStringAndClear();
}
