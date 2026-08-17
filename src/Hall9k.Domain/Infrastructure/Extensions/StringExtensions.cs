using System.Diagnostics.CodeAnalysis;

namespace Hall9k.Domain.Infrastructure.Extensions;

public static class StringExtensions
{
    public static bool IsBlank([NotNullWhen(false)] this string? value) => string.IsNullOrWhiteSpace(value);

    public static bool IsNotBlank([NotNullWhen(true)] this string? value) => !string.IsNullOrWhiteSpace(value);
}
