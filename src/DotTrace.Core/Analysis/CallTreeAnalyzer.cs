using System.Collections.Immutable;
using System.Text;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;

namespace DotTrace.Core.Analysis;

public sealed class CallTreeAnalyzer
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

    private readonly AnalysisOptions options;

    public CallTreeAnalyzer(AnalysisOptions? options = null)
    {
        this.options = options ?? new AnalysisOptions();
    }

    public async Task<AnalysisResult> AnalyzeAsync(string inputPath, string symbolSelector, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolSelector);

        EnsureMsBuildRegistered();

        var fullPath = Path.GetFullPath(inputPath);
        if (!File.Exists(fullPath))
        {
            throw new DotTraceException($"Input path does not exist: {fullPath}");
        }

        var extension = Path.GetExtension(fullPath);
        if (!extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            throw new DotTraceException("Only .sln and .csproj inputs are supported.");
        }

        var diagnostics = ImmutableArray.CreateBuilder<string>();
        using var workspace = MSBuildWorkspace.Create();
        workspace.RegisterWorkspaceFailedHandler(args => diagnostics.Add(args.Diagnostic.Message));

        Solution solution = extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
            ? await workspace.OpenSolutionAsync(fullPath, cancellationToken: cancellationToken)
            : (await workspace.OpenProjectAsync(fullPath, cancellationToken: cancellationToken)).Solution;

        var root = await ResolveRootMethodAsync(solution, symbolSelector, cancellationToken);
        var builder = new Builder(solution, options);
        var tree = await builder.BuildAsync(root, cancellationToken);
        return new AnalysisResult(tree, diagnostics.ToImmutable());
    }

    private static async Task<IMethodSymbol> ResolveRootMethodAsync(
        Solution solution,
        string symbolSelector,
        CancellationToken cancellationToken)
    {
        var selector = ParseSelector(symbolSelector);
        var candidates = new List<IMethodSymbol>();

        foreach (var project in solution.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is null)
            {
                continue;
            }

            var projectCandidates = compilation
                .GetSymbolsWithName(selector.MethodName, SymbolFilter.Member, cancellationToken)
                .OfType<IMethodSymbol>()
                .Where(method => method.MethodKind is not MethodKind.PropertyGet and not MethodKind.PropertySet)
                .Where(method => method.Locations.Any(location => location.IsInSource))
                .Where(method => selector.Matches(method));

            candidates.AddRange(projectCandidates);
        }

        if (candidates.Count == 0)
        {
            throw new DotTraceException($"No method matched symbol '{symbolSelector}'.");
        }

        if (candidates.Count > 1)
        {
            var matches = string.Join(
                Environment.NewLine,
                candidates
                    .Select(method => $" - {FormatMethod(method)}")
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal));

            throw new DotTraceException(
                $"The symbol '{symbolSelector}' is ambiguous. Use a fully-qualified signature. Matches:{Environment.NewLine}{matches}");
        }

        return candidates[0];
    }

    private static string ExtractMethodName(string normalizedSelector)
    {
        var openParenIndex = normalizedSelector.IndexOf('(');
        var prefix = openParenIndex >= 0 ? normalizedSelector[..openParenIndex] : normalizedSelector;
        var lastDotIndex = prefix.LastIndexOf('.');
        return lastDotIndex >= 0 ? prefix[(lastDotIndex + 1)..] : prefix;
    }

    private static string NormalizeSignature(string signature)
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

    private static void EnsureMsBuildRegistered()
    {
        if (!MSBuildLocator.IsRegistered)
        {
            MSBuildLocator.RegisterDefaults();
        }
    }

    private static SelectorPattern ParseSelector(string symbolSelector)
    {
        var normalizedSelector = NormalizeSignature(symbolSelector);
        var methodName = ExtractMethodName(normalizedSelector);

        if (normalizedSelector.Contains('('))
        {
            return new SelectorPattern(methodName, normalizedSelector, FullyQualifiedName: null);
        }

        return new SelectorPattern(methodName, Signature: null, normalizedSelector);
    }

    private sealed record SelectorPattern(string MethodName, string? Signature, string? FullyQualifiedName)
    {
        public bool Matches(IMethodSymbol method)
        {
            if (!string.Equals(method.Name, MethodName, StringComparison.Ordinal))
            {
                return false;
            }

            if (Signature is not null)
            {
                return NormalizeSignature(FormatMethod(method)) == Signature;
            }

            return NormalizeSignature(FormatMethod(method, includeParameters: false)) == FullyQualifiedName;
        }
    }

    private static string FormatMethod(IMethodSymbol method, bool includeParameters = true)
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

    private static string FormatParameter(IParameterSymbol parameter)
    {
        var prefix = parameter.RefKind switch
        {
            RefKind.Ref => "ref ",
            RefKind.Out => "out ",
            RefKind.In => "in ",
            _ => parameter.IsParams ? "params " : string.Empty
        };

        return prefix + parameter.Type.ToDisplayString(TypeDisplayFormat);
    }

    private sealed class Builder
    {
        private readonly AnalysisOptions options;
        private readonly Solution solution;
        private readonly Dictionary<DocumentId, SemanticModel> semanticModelCache = new();
        private readonly HashSet<string> expanded = new(StringComparer.Ordinal);

        public Builder(Solution solution, AnalysisOptions options)
        {
            this.solution = solution;
            this.options = options;
        }

        public Task<CallTreeNode> BuildAsync(IMethodSymbol root, CancellationToken cancellationToken)
        {
            return BuildNodeAsync(root, depth: 0, ImmutableHashSet<string>.Empty, cancellationToken);
        }

        private async Task<CallTreeNode> BuildNodeAsync(
            IMethodSymbol method,
            int depth,
            ImmutableHashSet<string> callStack,
            CancellationToken cancellationToken)
        {
            var nodeId = CreateNodeId(method);
            var display = FormatMethod(method);
            var location = GetLocation(method);

            if (callStack.Contains(nodeId))
            {
                return new CallTreeNode(nodeId, display, CallTreeNodeKind.Cycle, location, Array.Empty<CallTreeNode>());
            }

            if (expanded.Contains(nodeId))
            {
                return new CallTreeNode(nodeId, display, CallTreeNodeKind.Repeated, location, Array.Empty<CallTreeNode>());
            }

            if (options.MaxDepth is int maxDepth && depth >= maxDepth)
            {
                return new CallTreeNode(nodeId, display, CallTreeNodeKind.Truncated, location, Array.Empty<CallTreeNode>());
            }

            expanded.Add(nodeId);

            var childNodes = new List<CallTreeNode>();
            var nextStack = callStack.Add(nodeId);
            foreach (var call in await CollectCallsAsync(method, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                childNodes.Add(await BuildChildNodeAsync(call, depth + 1, nextStack, cancellationToken));
            }

            return new CallTreeNode(nodeId, display, CallTreeNodeKind.Source, location, childNodes);
        }

        private async Task<CallTreeNode> BuildChildNodeAsync(
            CallSite call,
            int depth,
            ImmutableHashSet<string> callStack,
            CancellationToken cancellationToken)
        {
            if (call.TargetSymbol is null)
            {
                return new CallTreeNode(
                    $"unresolved::{call.DisplayText}::{call.Location?.FilePath}:{call.Location?.Line}:{call.Location?.Column}",
                    call.DisplayText,
                    CallTreeNodeKind.Unresolved,
                    call.Location,
                    Array.Empty<CallTreeNode>());
            }

            if (!call.TargetSymbol.Locations.Any(location => location.IsInSource))
            {
                var externalDisplay = FormatMethod(call.TargetSymbol);
                return new CallTreeNode(
                    CreateNodeId(call.TargetSymbol),
                    externalDisplay,
                    CallTreeNodeKind.External,
                    GetLocation(call.TargetSymbol),
                    Array.Empty<CallTreeNode>());
            }

            return await BuildNodeAsync(call.TargetSymbol, depth, callStack, cancellationToken);
        }

        private async Task<IReadOnlyList<CallSite>> CollectCallsAsync(IMethodSymbol method, CancellationToken cancellationToken)
        {
            var calls = new List<CallSite>();

            foreach (var syntaxReference in method.DeclaringSyntaxReferences)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var declaration = await syntaxReference.GetSyntaxAsync(cancellationToken);
                var executableNode = GetExecutableNode(declaration);
                if (executableNode is null)
                {
                    continue;
                }

                var document = solution.GetDocument(executableNode.SyntaxTree);
                if (document is null)
                {
                    continue;
                }

                var semanticModel = await GetSemanticModelAsync(document, cancellationToken);
                var collector = new DirectCallCollector(semanticModel);
                collector.Visit(executableNode);
                calls.AddRange(collector.Calls);
            }

            return calls;
        }

        private async Task<SemanticModel> GetSemanticModelAsync(Document document, CancellationToken cancellationToken)
        {
            if (semanticModelCache.TryGetValue(document.Id, out var cached))
            {
                return cached;
            }

            var semanticModel = await document.GetSemanticModelAsync(cancellationToken)
                ?? throw new DotTraceException($"Failed to create a semantic model for '{document.FilePath}'.");

            semanticModelCache[document.Id] = semanticModel;
            return semanticModel;
        }

        private static SyntaxNode? GetExecutableNode(SyntaxNode declaration)
        {
            return declaration switch
            {
                MethodDeclarationSyntax method => (SyntaxNode?)method.Body ?? method.ExpressionBody?.Expression,
                ConstructorDeclarationSyntax constructor => (SyntaxNode?)constructor.Body ?? constructor.ExpressionBody?.Expression,
                DestructorDeclarationSyntax destructor => (SyntaxNode?)destructor.Body ?? destructor.ExpressionBody?.Expression,
                OperatorDeclarationSyntax op => (SyntaxNode?)op.Body ?? op.ExpressionBody?.Expression,
                ConversionOperatorDeclarationSyntax conversion => (SyntaxNode?)conversion.Body ?? conversion.ExpressionBody?.Expression,
                AccessorDeclarationSyntax accessor => (SyntaxNode?)accessor.Body ?? accessor.ExpressionBody?.Expression,
                LocalFunctionStatementSyntax localFunction => (SyntaxNode?)localFunction.Body ?? localFunction.ExpressionBody?.Expression,
                _ => declaration
            };
        }

        private static string CreateNodeId(IMethodSymbol method)
        {
            return method.GetDocumentationCommentId()
                ?? $"{FormatMethod(method)}@{method.Locations.FirstOrDefault()}";
        }

        private static SourceLocationInfo? GetLocation(IMethodSymbol method)
        {
            var location = method.Locations.FirstOrDefault(candidate => candidate.IsInSource);
            if (location is null)
            {
                return null;
            }

            var span = location.GetLineSpan();
            return new SourceLocationInfo(
                span.Path,
                span.StartLinePosition.Line + 1,
                span.StartLinePosition.Character + 1);
        }
    }

    private sealed class DirectCallCollector : CSharpSyntaxWalker
    {
        private readonly SemanticModel semanticModel;

        public DirectCallCollector(SemanticModel semanticModel)
        {
            this.semanticModel = semanticModel;
        }

        public List<CallSite> Calls { get; } = new();

        public override void VisitLocalFunctionStatement(LocalFunctionStatementSyntax node)
        {
        }

        public override void VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node)
        {
        }

        public override void VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node)
        {
        }

        public override void VisitAnonymousMethodExpression(AnonymousMethodExpressionSyntax node)
        {
        }

        public override void VisitClassDeclaration(ClassDeclarationSyntax node)
        {
        }

        public override void VisitStructDeclaration(StructDeclarationSyntax node)
        {
        }

        public override void VisitRecordDeclaration(RecordDeclarationSyntax node)
        {
        }

        public override void VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            AddCall(node, node.ToString());
            base.VisitInvocationExpression(node);
        }

        public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
        {
            AddCall(node, node.ToString());
            base.VisitObjectCreationExpression(node);
        }

        public override void VisitImplicitObjectCreationExpression(ImplicitObjectCreationExpressionSyntax node)
        {
            AddCall(node, node.ToString());
            base.VisitImplicitObjectCreationExpression(node);
        }

        public override void VisitConstructorInitializer(ConstructorInitializerSyntax node)
        {
            AddCall(node, node.ToString());
            base.VisitConstructorInitializer(node);
        }

        private void AddCall(SyntaxNode node, string displayText)
        {
            var symbolInfo = semanticModel.GetSymbolInfo(node);
            var targetSymbol = symbolInfo.Symbol as IMethodSymbol
                ?? symbolInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();

            Calls.Add(new CallSite(
                targetSymbol,
                CollapseWhitespace(displayText),
                CreateLocation(node.GetLocation())));
        }

        private static string CollapseWhitespace(string value)
        {
            return string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }

        private static SourceLocationInfo? CreateLocation(Location location)
        {
            if (!location.IsInSource)
            {
                return null;
            }

            var span = location.GetLineSpan();
            return new SourceLocationInfo(
                span.Path,
                span.StartLinePosition.Line + 1,
                span.StartLinePosition.Character + 1);
        }
    }

    private sealed record CallSite(IMethodSymbol? TargetSymbol, string DisplayText, SourceLocationInfo? Location);
}
