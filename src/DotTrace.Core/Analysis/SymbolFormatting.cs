using System.Text;
using Microsoft.CodeAnalysis;

namespace DotTrace.Core.Analysis;

internal static class SymbolFormatting
{
    private static readonly SymbolDisplayFormat TypeDisplayFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions:
            SymbolDisplayGenericsOptions.IncludeTypeParameters |
            SymbolDisplayGenericsOptions.IncludeVariance,
        miscellaneousOptions:
            SymbolDisplayMiscellaneousOptions.ExpandNullable |
            SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    public static string FormatMethod(IMethodSymbol method, bool includeParameters = true)
    {
        var containingType = method.ContainingType?.ToDisplayString(TypeDisplayFormat)
            ?? method.ContainingNamespace?.ToDisplayString();
        var methodName = method.MethodKind switch
        {
            MethodKind.Constructor or MethodKind.StaticConstructor => method.ContainingType?.Name ?? method.Name,
            MethodKind.Destructor => $"~{method.ContainingType?.Name}",
            _ => method.Name
        };

        var qualifiedName = string.IsNullOrWhiteSpace(containingType)
            ? methodName
            : $"{containingType}.{methodName}";

        if (!includeParameters)
        {
            return qualifiedName;
        }

        var parameters = string.Join(", ", method.Parameters.Select(FormatParameter));
        return $"{qualifiedName}({parameters})";
    }

    public static string NormalizeSignature(string signature)
    {
        var builder = new StringBuilder(signature.Length);
        foreach (var character in signature)
        {
            if (!char.IsWhiteSpace(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    public static SelectorPattern ParseSelector(string symbolSelector)
    {
        var normalizedSelector = NormalizeSignature(symbolSelector);
        var methodName = ExtractMethodName(normalizedSelector);

        return normalizedSelector.Contains('(')
            ? new SelectorPattern(methodName, normalizedSelector, FullyQualifiedName: null)
            : new SelectorPattern(methodName, Signature: null, normalizedSelector);
    }

    private static string FormatParameter(IParameterSymbol parameter)
    {
        var prefix = parameter.RefKind switch
        {
            RefKind.Ref => "ref ",
            RefKind.Out => "out ",
            RefKind.In => "in ",
            _ => parameter.IsParams ? "params " : string.Empty
        };

        return prefix + FormatType(parameter.Type);
    }

    public static string FormatType(ITypeSymbol type)
    {
        return type.ToDisplayString(TypeDisplayFormat);
    }

    private static string ExtractMethodName(string normalizedSelector)
    {
        var openParenIndex = normalizedSelector.IndexOf('(');
        var prefix = openParenIndex >= 0 ? normalizedSelector[..openParenIndex] : normalizedSelector;
        var lastDotIndex = prefix.LastIndexOf('.');
        return lastDotIndex >= 0 ? prefix[(lastDotIndex + 1)..] : prefix;
    }
}

internal sealed record SelectorPattern(string MethodName, string? Signature, string? FullyQualifiedName);
