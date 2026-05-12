using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Operations;

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
        private readonly List<CallGraphRootSymbol> rootSymbols = new();
        private readonly HashSet<string> rootSymbolKeys = new(StringComparer.Ordinal);

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

            foreach (var project in solution.Projects.OrderBy(project => project.FilePath ?? project.Name, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await CollectCompilerEntryPointAsync(project, cancellationToken);
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
                rootSymbols
                    .OrderBy(root => root.ProjectStableId, StringComparer.Ordinal)
                    .ThenBy(root => root.Kind)
                    .ThenBy(root => root.SymbolStableId, StringComparer.Ordinal)
                    .ToArray(),
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
                    AddSourceMethod(normalizedMethod, project, executableNode, semanticModel);
                    if (IsAspNetControllerAction(normalizedMethod))
                    {
                        AddRootSymbol(normalizedMethod, project, RootSymbolKind.AspNetControllerAction, metadataJson: null);
                    }
                }
            }
        }

        private async Task CollectCompilerEntryPointAsync(Project project, CancellationToken cancellationToken)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            var entryPoint = compilation?.GetEntryPoint(cancellationToken);
            if (entryPoint is null)
            {
                return;
            }

            await CaptureTopLevelEntryPointAsync(project, entryPoint.OriginalDefinition, cancellationToken);
            AddRootSymbol(entryPoint.OriginalDefinition, project, RootSymbolKind.CompilerEntryPoint, metadataJson: null);
        }

        private async Task CaptureTopLevelEntryPointAsync(
            Project project,
            IMethodSymbol entryPoint,
            CancellationToken cancellationToken)
        {
            var normalizedEntryPoint = entryPoint.OriginalDefinition;
            if (sourceStableIdsBySymbol.ContainsKey(normalizedEntryPoint))
            {
                return;
            }

            foreach (var document in project.Documents.OrderBy(document => document.FilePath ?? document.Name, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await document.GetSyntaxRootAsync(cancellationToken) is not CompilationUnitSyntax compilationUnit ||
                    !compilationUnit.Members.Any(member => member is GlobalStatementSyntax))
                {
                    continue;
                }

                var semanticModel = await GetSemanticModelAsync(document, cancellationToken);
                AddSourceMethod(normalizedEntryPoint, project, compilationUnit, semanticModel);
                return;
            }
        }

        private void AddSourceMethod(
            IMethodSymbol method,
            Project project,
            SyntaxNode executableNode,
            SemanticModel semanticModel)
        {
            var normalizedMethod = method.OriginalDefinition;
            var stableId = CreateStableId(normalizedMethod, project);
            sourceStableIdsBySymbol[normalizedMethod] = stableId;

            var symbol = CreateGraphSymbol(normalizedMethod, project, SymbolOriginKind.Source);
            symbolsByStableId.TryAdd(stableId, symbol);
            sourceBodiesByStableId.TryAdd(stableId, new SourceMethodBody(symbol, executableNode, semanticModel));
        }

        private void AddRootSymbol(
            IMethodSymbol method,
            Project project,
            RootSymbolKind kind,
            string? metadataJson)
        {
            var normalizedMethod = method.OriginalDefinition;
            if (!sourceStableIdsBySymbol.TryGetValue(normalizedMethod, out var sourceStableId))
            {
                diagnostics.Add($"Skipped {FormatRootKind(kind)} root '{SymbolFormatting.FormatMethod(normalizedMethod)}' because no captured source symbol matched it.");
                return;
            }

            if (!symbolsByStableId.TryGetValue(sourceStableId, out var symbol) || symbol.ProjectStableId is null)
            {
                diagnostics.Add($"Skipped {FormatRootKind(kind)} root '{SymbolFormatting.FormatMethod(normalizedMethod)}' because it did not resolve to a source project.");
                return;
            }

            var projectStableId = CreateProjectStableId(project);
            if (!string.Equals(symbol.ProjectStableId, projectStableId, StringComparison.Ordinal))
            {
                diagnostics.Add($"Skipped {FormatRootKind(kind)} root '{SymbolFormatting.FormatMethod(normalizedMethod)}' because it resolved to a different source project.");
                return;
            }

            var key = $"{projectStableId}\n{sourceStableId}\n{kind}";
            if (!rootSymbolKeys.Add(key))
            {
                return;
            }

            rootSymbols.Add(new CallGraphRootSymbol(projectStableId, sourceStableId, kind, metadataJson));
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
                ConstructorDeclarationSyntax constructor => constructor,
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

        private static bool IsAspNetControllerAction(IMethodSymbol method)
        {
            if (method.MethodKind != MethodKind.Ordinary ||
                method.DeclaredAccessibility != Accessibility.Public ||
                method.IsStatic ||
                method.ContainingType is null ||
                HasAttribute(method, "Microsoft.AspNetCore.Mvc.NonActionAttribute"))
            {
                return false;
            }

            var containingType = method.ContainingType;
            if (IsAspNetControllerType(containingType))
            {
                return true;
            }

            return containingType.Name.EndsWith("Controller", StringComparison.Ordinal) &&
                HasHttpMethodAttribute(method);
        }

        private static bool IsAspNetControllerType(INamedTypeSymbol type)
        {
            return InheritsFrom(type, "Microsoft.AspNetCore.Mvc.ControllerBase") ||
                InheritsFrom(type, "Microsoft.AspNetCore.Mvc.Controller") ||
                HasAttribute(type, "Microsoft.AspNetCore.Mvc.ApiControllerAttribute") ||
                HasAttribute(type, "Microsoft.AspNetCore.Mvc.ControllerAttribute");
        }

        private static bool HasHttpMethodAttribute(IMethodSymbol method)
        {
            return method.GetAttributes().Any(attribute =>
                InheritsFrom(attribute.AttributeClass, "Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute"));
        }

        private static bool HasAttribute(ISymbol symbol, string fullMetadataName)
        {
            return symbol.GetAttributes().Any(attribute =>
                HasFullMetadataName(attribute.AttributeClass, fullMetadataName));
        }

        private static bool InheritsFrom(INamedTypeSymbol? type, string fullMetadataName)
        {
            for (var current = type; current is not null; current = current.BaseType)
            {
                if (HasFullMetadataName(current, fullMetadataName))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasFullMetadataName(INamedTypeSymbol? type, string fullMetadataName)
        {
            return type is not null &&
                string.Equals(GetFullMetadataName(type), fullMetadataName, StringComparison.Ordinal);
        }

        private static string GetFullMetadataName(INamedTypeSymbol type)
        {
            var prefix = type.ContainingType is not null
                ? GetFullMetadataName(type.ContainingType)
                : type.ContainingNamespace is { IsGlobalNamespace: false }
                    ? type.ContainingNamespace.ToDisplayString()
                    : string.Empty;

            return string.IsNullOrEmpty(prefix)
                ? type.MetadataName
                : $"{prefix}.{type.MetadataName}";
        }

        private static string FormatRootKind(RootSymbolKind kind)
        {
            return kind switch
            {
                RootSymbolKind.CompilerEntryPoint => "compiler entry point",
                RootSymbolKind.AspNetControllerAction => "ASP.NET controller action",
                _ => kind.ToString()
            };
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
            AddMethodCall(node, FormatInvocation(node));
            base.VisitInvocationExpression(node);
        }

        public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
        {
            AddMethodCall(node, FormatObjectCreation(node));
            base.VisitObjectCreationExpression(node);
        }

        public override void VisitImplicitObjectCreationExpression(ImplicitObjectCreationExpressionSyntax node)
        {
            AddMethodCall(node, FormatImplicitObjectCreation(node));
            base.VisitImplicitObjectCreationExpression(node);
        }

        public override void VisitConstructorInitializer(ConstructorInitializerSyntax node)
        {
            AddMethodCall(node, FormatConstructorInitializer(node));
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
            var targetSymbol = GetTargetMethod(node);

            Calls.Add(new CallSite(
                targetSymbol,
                displayText,
                CreateLocation(node.GetLocation())));
        }

        private IMethodSymbol? GetTargetMethod(SyntaxNode node)
        {
            var operation = semanticModel.GetOperation(node);
            if (operation is IInvocationOperation invocation)
            {
                return invocation.TargetMethod;
            }

            if (operation is IObjectCreationOperation objectCreation)
            {
                return objectCreation.Constructor;
            }

            var symbolInfo = semanticModel.GetSymbolInfo(node);
            return symbolInfo.Symbol as IMethodSymbol
                ?? symbolInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
        }

        private string FormatInvocation(InvocationExpressionSyntax node)
        {
            var operation = semanticModel.GetOperation(node) as IInvocationOperation;
            return $"{FormatInvocationName(node)}({FormatArguments(node.ArgumentList.Arguments, operation?.Arguments ?? ImmutableArray<IArgumentOperation>.Empty)})";
        }

        private string FormatObjectCreation(ObjectCreationExpressionSyntax node)
        {
            var operation = semanticModel.GetOperation(node) as IObjectCreationOperation;
            var arguments = node.ArgumentList?.Arguments ?? default;
            return $"new {NormalizeSyntax(node.Type)}({FormatArguments(arguments, operation?.Arguments ?? ImmutableArray<IArgumentOperation>.Empty)})";
        }

        private string FormatImplicitObjectCreation(ImplicitObjectCreationExpressionSyntax node)
        {
            var operation = semanticModel.GetOperation(node) as IObjectCreationOperation;
            return $"new({FormatArguments(node.ArgumentList.Arguments, operation?.Arguments ?? ImmutableArray<IArgumentOperation>.Empty)})";
        }

        private string FormatConstructorInitializer(ConstructorInitializerSyntax node)
        {
            var operation = semanticModel.GetOperation(node) as IInvocationOperation;
            return $"{node.ThisOrBaseKeyword.ValueText}({FormatArguments(node.ArgumentList.Arguments, operation?.Arguments ?? ImmutableArray<IArgumentOperation>.Empty)})";
        }

        private string FormatArguments(
            SeparatedSyntaxList<ArgumentSyntax> arguments,
            ImmutableArray<IArgumentOperation> operationArguments)
        {
            return string.Join(
                ", ",
                arguments.Select(argument => FormatArgument(argument, FindArgumentOperation(argument, operationArguments))));
        }

        private string FormatArgument(ArgumentSyntax syntax, IArgumentOperation? operation)
        {
            var builder = new StringBuilder();
            if (syntax.NameColon is not null)
            {
                builder.Append(syntax.NameColon.Name.Identifier.ValueText);
                builder.Append(": ");
            }

            if (!syntax.RefKindKeyword.IsKind(SyntaxKind.None))
            {
                builder.Append(syntax.RefKindKeyword.ValueText);
                builder.Append(' ');
            }

            builder.Append(NormalizeSyntax(syntax.Expression));
            builder.Append(": ");
            builder.Append(FormatConvertedType(syntax, operation));
            return builder.ToString();
        }

        private string FormatConvertedType(ArgumentSyntax syntax, IArgumentOperation? operation)
        {
            var typeInfo = semanticModel.GetTypeInfo(syntax.Expression);
            var convertedType = typeInfo.ConvertedType
                ?? operation?.Value.Type
                ?? operation?.Parameter?.Type
                ?? typeInfo.Type;

            return convertedType is null
                ? "unknown"
                : SymbolFormatting.FormatType(convertedType);
        }

        private static IArgumentOperation? FindArgumentOperation(
            ArgumentSyntax syntax,
            ImmutableArray<IArgumentOperation> operationArguments)
        {
            return operationArguments.FirstOrDefault(argument => argument.Syntax.Span == syntax.Span);
        }

        private static string FormatInvocationName(InvocationExpressionSyntax node)
        {
            return node.Expression switch
            {
                MemberAccessExpressionSyntax memberAccess => NormalizeSyntax(memberAccess.Name),
                MemberBindingExpressionSyntax memberBinding => NormalizeSyntax(memberBinding.Name),
                SimpleNameSyntax simpleName => NormalizeSyntax(simpleName),
                _ => NormalizeSyntax(node.Expression)
            };
        }

        private static string NormalizeSyntax(SyntaxNode node)
        {
            return node.NormalizeWhitespace().ToFullString();
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
