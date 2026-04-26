using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;

namespace DotTrace.Core.Analysis;

public sealed class CallGraphBuilder
{
    public async Task<CallGraphBuildResult> BuildAsync(string inputPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);

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

        var solution = extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
            ? await workspace.OpenSolutionAsync(fullPath, cancellationToken: cancellationToken)
            : (await workspace.OpenProjectAsync(fullPath, cancellationToken: cancellationToken)).Solution;

        var builder = new WorkspaceGraphBuilder(solution, fullPath, diagnostics);
        return await builder.BuildAsync(cancellationToken);
    }

    private static void EnsureMsBuildRegistered()
    {
        if (!MSBuildLocator.IsRegistered)
        {
            MSBuildLocator.RegisterDefaults();
        }
    }

    private sealed class WorkspaceGraphBuilder
    {
        private readonly Solution solution;
        private readonly string inputPath;
        private readonly ImmutableArray<string>.Builder diagnostics;
        private readonly Dictionary<string, CallGraphProject> projectsByStableId = new(StringComparer.Ordinal);
        private readonly Dictionary<string, CallGraphSymbol> symbolsByStableId = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SourceMethodBody> sourceBodiesByStableId = new(StringComparer.Ordinal);
        private readonly Dictionary<ISymbol, string> sourceStableIdsBySymbol = new(SymbolEqualityComparer.Default);
        private readonly Dictionary<string, SemanticModel> semanticModelsByDocumentId = new(StringComparer.Ordinal);

        public WorkspaceGraphBuilder(
            Solution solution,
            string inputPath,
            ImmutableArray<string>.Builder diagnostics)
        {
            this.solution = solution;
            this.inputPath = inputPath;
            this.diagnostics = diagnostics;
        }

        public async Task<CallGraphBuildResult> BuildAsync(CancellationToken cancellationToken)
        {
            foreach (var project in solution.Projects.OrderBy(project => project.FilePath ?? project.Name, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddProject(project);
                await CollectSourceSymbolsAsync(project, cancellationToken);
            }

            var calls = new List<CallGraphCall>();
            foreach (var body in sourceBodiesByStableId.Values.OrderBy(body => body.Symbol.StableId, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                calls.AddRange(CollectCalls(body, cancellationToken));
            }

            var fingerprint = await ComputeWorkspaceFingerprintAsync(cancellationToken);
            return new CallGraphBuildResult(
                inputPath,
                fingerprint,
                GetToolVersion(),
                projectsByStableId.Values.OrderBy(project => project.StableId, StringComparer.Ordinal).ToArray(),
                symbolsByStableId.Values.OrderBy(symbol => symbol.StableId, StringComparer.Ordinal).ToArray(),
                calls,
                diagnostics.ToImmutable());
        }

        private void AddProject(Project project)
        {
            var stableId = CreateProjectStableId(project);
            if (projectsByStableId.ContainsKey(stableId))
            {
                return;
            }

            projectsByStableId.Add(
                stableId,
                new CallGraphProject(
                    stableId,
                    project.Name,
                    project.AssemblyName ?? project.Name,
                    project.FilePath is null ? string.Empty : Path.GetFullPath(project.FilePath)));
        }

        private async Task CollectSourceSymbolsAsync(Project project, CancellationToken cancellationToken)
        {
            foreach (var document in project.Documents.OrderBy(document => document.FilePath ?? document.Name, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var root = await document.GetSyntaxRootAsync(cancellationToken);
                if (root is null)
                {
                    continue;
                }

                var semanticModel = await GetSemanticModelAsync(document, cancellationToken);
                foreach (var declaration in root.DescendantNodes().Where(IsExecutableDeclaration))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var executableNode = GetExecutableNode(declaration);
                    if (executableNode is null)
                    {
                        continue;
                    }

                    if (semanticModel.GetDeclaredSymbol(declaration, cancellationToken) is not IMethodSymbol methodSymbol)
                    {
                        continue;
                    }

                    var normalizedMethod = methodSymbol.OriginalDefinition;
                    var stableId = CreateStableId(normalizedMethod, project);
                    sourceStableIdsBySymbol[normalizedMethod] = stableId;

                    var symbol = CreateGraphSymbol(normalizedMethod, project, SymbolOriginKind.Source);
                    symbolsByStableId.TryAdd(stableId, symbol);
                    sourceBodiesByStableId.TryAdd(stableId, new SourceMethodBody(symbol, executableNode, semanticModel));
                }
            }
        }

        private IReadOnlyList<CallGraphCall> CollectCalls(SourceMethodBody body, CancellationToken cancellationToken)
        {
            var collector = new DirectCallCollector(body.SemanticModel);
            collector.Visit(body.ExecutableNode);

            var calls = new List<CallGraphCall>();
            var ordinal = 0;
            foreach (var call in collector.Calls)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var calleeStableId = call.TargetSymbol is null
                    ? null
                    : EnsureCalleeSymbol(call.TargetSymbol.OriginalDefinition);

                calls.Add(new CallGraphCall(
                    body.Symbol.StableId,
                    calleeStableId,
                    call.DisplayText,
                    call.Location,
                    ordinal));
                ordinal++;
            }

            return calls;
        }

        private string EnsureCalleeSymbol(IMethodSymbol targetSymbol)
        {
            if (sourceStableIdsBySymbol.TryGetValue(targetSymbol, out var existingSourceStableId))
            {
                return existingSourceStableId;
            }

            var sourceProject = FindSourceProject(targetSymbol);
            if (sourceProject is not null)
            {
                var sourceStableId = CreateStableId(targetSymbol, sourceProject);
                if (!symbolsByStableId.ContainsKey(sourceStableId))
                {
                    AddProject(sourceProject);
                    symbolsByStableId.Add(sourceStableId, CreateGraphSymbol(targetSymbol, sourceProject, SymbolOriginKind.Source));
                }

                return sourceStableId;
            }

            var externalStableId = CreateExternalStableId(targetSymbol);
            symbolsByStableId.TryAdd(externalStableId, CreateGraphSymbol(targetSymbol, project: null, SymbolOriginKind.External));
            return externalStableId;
        }

        private Project? FindSourceProject(IMethodSymbol symbol)
        {
            var location = symbol.Locations.FirstOrDefault(location => location.IsInSource && location.SourceTree is not null);
            if (location?.SourceTree is null)
            {
                return null;
            }

            return solution.GetDocument(location.SourceTree)?.Project;
        }

        private async Task<SemanticModel> GetSemanticModelAsync(Document document, CancellationToken cancellationToken)
        {
            var cacheKey = document.Id.Id.ToString();
            if (semanticModelsByDocumentId.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }

            var semanticModel = await document.GetSemanticModelAsync(cancellationToken)
                ?? throw new DotTraceException($"Failed to create a semantic model for '{document.FilePath}'.");

            semanticModelsByDocumentId[cacheKey] = semanticModel;
            return semanticModel;
        }

        private CallGraphSymbol CreateGraphSymbol(IMethodSymbol method, Project? project, SymbolOriginKind originKind)
        {
            var stableId = originKind == SymbolOriginKind.Source && project is not null
                ? CreateStableId(method, project)
                : CreateExternalStableId(method);
            var qualifiedName = SymbolFormatting.FormatMethod(method, includeParameters: false);
            var signatureText = SymbolFormatting.FormatMethod(method);

            return new CallGraphSymbol(
                stableId,
                project is null ? null : CreateProjectStableId(project),
                qualifiedName,
                signatureText,
                SymbolFormatting.NormalizeSignature(qualifiedName),
                SymbolFormatting.NormalizeSignature(signatureText),
                originKind,
                GetLocation(method));
        }

        private static bool IsExecutableDeclaration(SyntaxNode node)
        {
            return node switch
            {
                MethodDeclarationSyntax method => HasExecutableBody(method.Body, method.ExpressionBody),
                ConstructorDeclarationSyntax constructor => HasExecutableBody(constructor.Body, constructor.ExpressionBody),
                DestructorDeclarationSyntax destructor => HasExecutableBody(destructor.Body, destructor.ExpressionBody),
                OperatorDeclarationSyntax op => HasExecutableBody(op.Body, op.ExpressionBody),
                ConversionOperatorDeclarationSyntax conversion => HasExecutableBody(conversion.Body, conversion.ExpressionBody),
                AccessorDeclarationSyntax accessor => HasExecutableBody(accessor.Body, accessor.ExpressionBody),
                LocalFunctionStatementSyntax localFunction => HasExecutableBody(localFunction.Body, localFunction.ExpressionBody),
                _ => false
            };
        }

        private static bool HasExecutableBody(BlockSyntax? body, ArrowExpressionClauseSyntax? expressionBody)
        {
            return body is not null || expressionBody is not null;
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
                _ => null
            };
        }

        private static SourceLocationInfo? GetLocation(IMethodSymbol method)
        {
            var location = method.Locations.FirstOrDefault(candidate => candidate.IsInSource);
            if (location is null)
            {
                return null;
            }

            return CreateLocation(location);
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

        private static string CreateProjectStableId(Project project)
        {
            var projectPath = project.FilePath is null ? project.Name : Path.GetFullPath(project.FilePath);
            return $"{project.AssemblyName ?? project.Name}|{projectPath}";
        }

        private static string CreateStableId(IMethodSymbol method, Project project)
        {
            var symbolId = method.GetDocumentationCommentId()
                ?? $"{SymbolFormatting.FormatMethod(method)}@{FormatLocation(method.Locations.FirstOrDefault(location => location.IsInSource))}";
            return $"source::{CreateProjectStableId(project)}::{symbolId}";
        }

        private static string CreateExternalStableId(IMethodSymbol method)
        {
            var assemblyIdentity = method.ContainingAssembly?.Identity.GetDisplayName()
                ?? method.ContainingAssembly?.Name
                ?? "unknown";
            var symbolId = method.GetDocumentationCommentId()
                ?? SymbolFormatting.FormatMethod(method);
            return $"external::{assemblyIdentity}::{symbolId}";
        }

        private static string FormatLocation(Location? location)
        {
            if (location is null || !location.IsInSource)
            {
                return "unknown";
            }

            var span = location.GetLineSpan();
            return $"{span.Path}:{span.StartLinePosition.Line + 1}:{span.StartLinePosition.Character + 1}";
        }

        private async Task<string> ComputeWorkspaceFingerprintAsync(CancellationToken cancellationToken)
        {
            var paths = new SortedSet<string>(StringComparer.Ordinal);
            AddIfFile(paths, inputPath);
            foreach (var project in solution.Projects)
            {
                AddIfFile(paths, project.FilePath);
                foreach (var document in project.Documents)
                {
                    AddIfFile(paths, document.FilePath);
                }
            }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AppendHashText(hash, path);
                await using var stream = File.OpenRead(path);
                var buffer = new byte[64 * 1024];
                int read;
                while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                }
            }

            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }

        private static void AddIfFile(ISet<string> paths, string? path)
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                paths.Add(Path.GetFullPath(path));
            }
        }

        private static void AppendHashText(IncrementalHash hash, string value)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(value));
            hash.AppendData([0]);
        }

        private static string GetToolVersion()
        {
            return typeof(CallGraphBuilder).Assembly.GetName().Version?.ToString()
                ?? "unknown";
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
            AddMethodCall(node, node.ToString());
            base.VisitInvocationExpression(node);
        }

        public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
        {
            AddMethodCall(node, node.ToString());
            base.VisitObjectCreationExpression(node);
        }

        public override void VisitImplicitObjectCreationExpression(ImplicitObjectCreationExpressionSyntax node)
        {
            AddMethodCall(node, node.ToString());
            base.VisitImplicitObjectCreationExpression(node);
        }

        public override void VisitConstructorInitializer(ConstructorInitializerSyntax node)
        {
            AddMethodCall(node, node.ToString());
            base.VisitConstructorInitializer(node);
        }

        public override void VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
        {
            AddPropertyAccess(node, node.ToString());
            base.VisitMemberAccessExpression(node);
        }

        public override void VisitMemberBindingExpression(MemberBindingExpressionSyntax node)
        {
            AddPropertyAccess(node, node.ToString());
            base.VisitMemberBindingExpression(node);
        }

        public override void VisitElementAccessExpression(ElementAccessExpressionSyntax node)
        {
            AddPropertyAccess(node, node.ToString());
            base.VisitElementAccessExpression(node);
        }

        public override void VisitIdentifierName(IdentifierNameSyntax node)
        {
            if (!IsNestedPropertyName(node))
            {
                AddPropertyAccess(node, node.ToString());
            }

            base.VisitIdentifierName(node);
        }

        private void AddMethodCall(SyntaxNode node, string displayText)
        {
            var symbolInfo = semanticModel.GetSymbolInfo(node);
            var targetSymbol = symbolInfo.Symbol as IMethodSymbol
                ?? symbolInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();

            Calls.Add(new CallSite(
                targetSymbol,
                CollapseWhitespace(displayText),
                CreateLocation(node.GetLocation())));
        }

        private void AddPropertyAccess(ExpressionSyntax node, string displayText)
        {
            if (IsNameOfArgument(node))
            {
                return;
            }

            var symbolInfo = semanticModel.GetSymbolInfo(node);
            var propertySymbol = symbolInfo.Symbol as IPropertySymbol
                ?? symbolInfo.CandidateSymbols.OfType<IPropertySymbol>().FirstOrDefault();
            if (propertySymbol is null)
            {
                return;
            }

            var accessKind = GetAccessKind(node);
            if (accessKind is AccessKind.Read or AccessKind.ReadWrite && propertySymbol.GetMethod is not null)
            {
                AddAccessorCall(propertySymbol.GetMethod, displayText, node);
            }

            if (accessKind is AccessKind.Write or AccessKind.ReadWrite && propertySymbol.SetMethod is not null)
            {
                AddAccessorCall(propertySymbol.SetMethod, displayText, node);
            }
        }

        private void AddAccessorCall(IMethodSymbol accessor, string displayText, SyntaxNode node)
        {
            Calls.Add(new CallSite(
                accessor,
                CollapseWhitespace(displayText),
                CreateLocation(node.GetLocation())));
        }

        private static AccessKind GetAccessKind(ExpressionSyntax node)
        {
            if (node.Parent is AssignmentExpressionSyntax assignment && assignment.Left == node)
            {
                return assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
                    ? AccessKind.Write
                    : AccessKind.ReadWrite;
            }

            if (node.Parent is PrefixUnaryExpressionSyntax prefix &&
                (prefix.IsKind(SyntaxKind.PreIncrementExpression) || prefix.IsKind(SyntaxKind.PreDecrementExpression)))
            {
                return AccessKind.ReadWrite;
            }

            if (node.Parent is PostfixUnaryExpressionSyntax postfix &&
                (postfix.IsKind(SyntaxKind.PostIncrementExpression) || postfix.IsKind(SyntaxKind.PostDecrementExpression)))
            {
                return AccessKind.ReadWrite;
            }

            if (node.Parent is ArgumentSyntax argument)
            {
                return argument.RefKindKeyword.Kind() switch
                {
                    SyntaxKind.OutKeyword => AccessKind.Write,
                    SyntaxKind.RefKeyword => AccessKind.ReadWrite,
                    _ => AccessKind.Read
                };
            }

            return AccessKind.Read;
        }

        private static bool IsNestedPropertyName(IdentifierNameSyntax node)
        {
            return node.Parent switch
            {
                MemberAccessExpressionSyntax memberAccess when memberAccess.Name == node => true,
                MemberBindingExpressionSyntax memberBinding when memberBinding.Name == node => true,
                _ => false
            };
        }

        private static bool IsNameOfArgument(ExpressionSyntax node)
        {
            return node.Ancestors().OfType<InvocationExpressionSyntax>().Any(invocation =>
                invocation.Expression is IdentifierNameSyntax identifier &&
                string.Equals(identifier.Identifier.ValueText, "nameof", StringComparison.Ordinal));
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

    private sealed record SourceMethodBody(
        CallGraphSymbol Symbol,
        SyntaxNode ExecutableNode,
        SemanticModel SemanticModel);

    private sealed record CallSite(IMethodSymbol? TargetSymbol, string DisplayText, SourceLocationInfo? Location);

    private enum AccessKind
    {
        Read,
        Write,
        ReadWrite
    }
}
