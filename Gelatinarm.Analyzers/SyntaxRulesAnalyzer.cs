using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Gelatinarm.Analyzers
{
    /// <summary>
    ///     Shapes visible in one method at a time: a catch that reports and rethrows, or that logs by
    ///     hand beside ErrorHandler, a log call before a throw, a credential or a stream URL in a log
    ///     call, a token source replaced by hand, a preference key as a literal, a doc comment on
    ///     nothing, a page holding its own view model, a blank line before a closing brace.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class SyntaxRulesAnalyzer : DiagnosticAnalyzer
    {
        private static readonly Regex Credential = new Regex(@"(?i)\w*(secret|password|accesstoken|apikey)\w*|\w*token\b", RegexOptions.Compiled);
        private static readonly Regex StreamUrl = new Regex(@"TranscodingUrl|\b\w*(?:[Ss]tream|[Mm]edia|[Tt]ranscod|[Ff]lac|[Hh]ls)\w*Ur[li]\b", RegexOptions.Compiled);
        private static readonly Regex LogMethod = new Regex("^Log(Trace|Debug|Information|Warning|Error|Critical)$", RegexOptions.Compiled);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(
            Descriptors.CatchReportsThenRethrows, Descriptors.CredentialLogged, Descriptors.StreamUrlLogged,
            Descriptors.TokenSourceReplacedByHand, Descriptors.PreferenceKeyLiteral, Descriptors.OrphanedDocComment,
            Descriptors.PageViewModelOutsideBase, Descriptors.BlankLineBeforeClosingBrace, Descriptors.AnyThroughConditionalOnCounted,
            Descriptors.TypeTestSubsumed, Descriptors.HandBuiltCommand, Descriptors.HandWrittenObservableProperty, Descriptors.HandWrittenFactory,
            Descriptors.ConditionalAfterDereference, Descriptors.TaskDiscardedWithFireAndForget, Descriptors.AdjacentIfsOneBody,
            Descriptors.ConsecutiveLogCalls, Descriptors.UsingOutOfOrder, Descriptors.CatchLogsByHand, Descriptors.LogThenThrow,
            Descriptors.RethrowOnlyCatch, Descriptors.StructureComment, Descriptors.CancellationLogged, Descriptors.NullableBoolCoalesced,
            Descriptors.AsyncLambdaOnlyAwaits, Descriptors.CollectionReshaped, Descriptors.LiteralRepeatsArray, Descriptors.AsyncMethodOnlyAwaits);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(AnalyzeCatch, SyntaxKind.CatchClause);
            context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
            context.RegisterSyntaxNodeAction(AnalyzeObjectCreation, SyntaxKind.ObjectCreationExpression);
            context.RegisterSyntaxNodeAction(AnalyzeBlock, SyntaxKind.Block);
            context.RegisterSyntaxNodeAction(AnalyzeSwitchSection, SyntaxKind.SwitchSection);
            context.RegisterSyntaxNodeAction(AnalyzeProperty, SyntaxKind.PropertyDeclaration);
            context.RegisterSyntaxTreeAction(AnalyzeDocComments);
            context.RegisterSyntaxTreeAction(AnalyzeUsings);
            context.RegisterSyntaxNodeAction(AnalyzeTypeTests, SyntaxKind.LogicalOrExpression, SyntaxKind.SwitchSection);
            context.RegisterSyntaxNodeAction(AnalyzeToolkitShapes, SyntaxKind.ObjectCreationExpression, SyntaxKind.PropertyDeclaration);
            context.RegisterSyntaxNodeAction(AnalyzeClosingBrace, SyntaxKind.ClassDeclaration, SyntaxKind.StructDeclaration,
                SyntaxKind.InterfaceDeclaration, SyntaxKind.EnumDeclaration, SyntaxKind.RecordDeclaration, SyntaxKind.NamespaceDeclaration,
                SyntaxKind.Block);
            context.RegisterSyntaxNodeAction(AnalyzeDiscardedTask, SyntaxKind.SimpleAssignmentExpression);
            context.RegisterSyntaxTreeAction(AnalyzeStructureComments);
            context.RegisterSyntaxNodeAction(AnalyzeCoalesce, SyntaxKind.CoalesceExpression);
            context.RegisterSyntaxNodeAction(AnalyzeAsyncLambda, SyntaxKind.ParenthesizedLambdaExpression, SyntaxKind.SimpleLambdaExpression);
            context.RegisterSyntaxNodeAction(AnalyzeLiteralsAgainstArrays, SyntaxKind.ClassDeclaration);
            context.RegisterSyntaxNodeAction(AnalyzeAsyncMethod, SyntaxKind.MethodDeclaration);
        }

        /// <summary>
        ///     An async Task method whose body is one awaited call (ConfigureAwait or not) of the type it
        ///     returns; `return await` that changes the task's type is left alone.
        /// </summary>
        private static void AnalyzeAsyncMethod(SyntaxNodeAnalysisContext context)
        {
            var method = (MethodDeclarationSyntax)context.Node;
            if (!method.Modifiers.Any(SyntaxKind.AsyncKeyword) || method.Body?.Statements.Count != 1)
            {
                return;
            }

            var statement = method.Body.Statements[0];
            var awaited = (statement as ExpressionStatementSyntax)?.Expression as AwaitExpressionSyntax
                          ?? (statement as ReturnStatementSyntax)?.Expression as AwaitExpressionSyntax;
            if (!(awaited?.Expression is InvocationExpressionSyntax call))
            {
                return;
            }

            if (call.Expression is MemberAccessExpressionSyntax configured && configured.Name.Identifier.Text == "ConfigureAwait" &&
                configured.Expression is InvocationExpressionSyntax inner)
            {
                call = inner;
            }

            var symbol = context.SemanticModel.GetDeclaredSymbol(method, context.CancellationToken);
            var callType = context.SemanticModel.GetTypeInfo(call, context.CancellationToken).Type;
            if (symbol == null || callType == null || symbol.ReturnsVoid)
            {
                return;
            }

            var sameTask = statement is ReturnStatementSyntax
                ? SymbolEqualityComparer.Default.Equals(callType, symbol.ReturnType)
                : callType.Name == "Task" && symbol.ReturnType.Name == "Task" && (symbol.ReturnType as INamedTypeSymbol)?.IsGenericType != true;
            if (sameTask)
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.AsyncMethodOnlyAwaits, method.Identifier.GetLocation(), method.Identifier.Text));
            }
        }

        /// <summary>
        ///     A string literal whose comma- or pipe-separated items are exactly the items of a static string
        ///     array in the same class.
        /// </summary>
        private static void AnalyzeLiteralsAgainstArrays(SyntaxNodeAnalysisContext context)
        {
            var type = (ClassDeclarationSyntax)context.Node;
            var arrays = new List<(string Name, HashSet<string> Items, SyntaxNode Initializer)>();
            foreach (var field in type.Members.OfType<FieldDeclarationSyntax>().Where(f => f.Modifiers.Any(SyntaxKind.StaticKeyword)))
            {
                foreach (var variable in field.Declaration.Variables)
                {
                    var initializer = variable.Initializer?.Value;
                    var items = (initializer as InitializerExpressionSyntax ?? (initializer as ImplicitArrayCreationExpressionSyntax)?.Initializer ??
                                 (initializer as ArrayCreationExpressionSyntax)?.Initializer)?.Expressions;
                    if (items == null || items.Value.Count < 2 || !items.Value.All(e => e.IsKind(SyntaxKind.StringLiteralExpression)))
                    {
                        continue;
                    }

                    arrays.Add((variable.Identifier.Text, new HashSet<string>(items.Value.Select(e => ((LiteralExpressionSyntax)e).Token.ValueText), StringComparer.OrdinalIgnoreCase), initializer));
                }
            }

            if (arrays.Count == 0)
            {
                return;
            }

            foreach (var literal in type.DescendantNodes().OfType<LiteralExpressionSyntax>().Where(l => l.IsKind(SyntaxKind.StringLiteralExpression)))
            {
                if (arrays.Any(a => a.Initializer.Span.Contains(literal.Span)))
                {
                    continue;
                }

                foreach (var separator in new[] { ',', '|' })
                {
                    var items = new HashSet<string>(literal.Token.ValueText.Split(separator).Select(s => s.Trim()).Where(s => s.Length > 0), StringComparer.OrdinalIgnoreCase);
                    var array = items.Count > 1 ? arrays.FirstOrDefault(a => a.Items.SetEquals(items)) : default;
                    if (array.Name != null)
                    {
                        context.ReportDiagnostic(Diagnostic.Create(Descriptors.LiteralRepeatsArray, literal.GetLocation(), literal.Token.ValueText, array.Name, separator.ToString()));
                        break;
                    }
                }
            }
        }

        /// <summary>
        ///     ToList() or ToArray() on the result of a method in this compilation whose every return is
        ///     the other shape (an array or ToArray(); a List or ToList()).
        /// </summary>
        private static void CheckReshapedCollection(SyntaxNodeAnalysisContext context, InvocationExpressionSyntax invocation, string conversion)
        {
            if (invocation.ArgumentList.Arguments.Count > 0 || !((invocation.Expression as MemberAccessExpressionSyntax)?.Expression is InvocationExpressionSyntax inner))
            {
                return;
            }

            var method = context.SemanticModel.GetSymbolInfo(inner, context.CancellationToken).Symbol as IMethodSymbol;
            if (!(method?.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(context.CancellationToken) is MethodDeclarationSyntax declaration))
            {
                return;
            }

            var returns = declaration.ExpressionBody != null
                ? new List<ExpressionSyntax> { declaration.ExpressionBody.Expression }
                : declaration.Body?.DescendantNodes(n => !(n is LambdaExpressionSyntax) && !(n is LocalFunctionStatementSyntax))
                    .OfType<ReturnStatementSyntax>().Select(r => r.Expression).Where(e => e != null).ToList();
            if (returns == null || returns.Count == 0)
            {
                return;
            }

            var toList = conversion == "ToList";
            bool OtherShape(ExpressionSyntax e)
            {
                var call = (e as InvocationExpressionSyntax)?.Expression as MemberAccessExpressionSyntax;
                return toList
                    ? e is ArrayCreationExpressionSyntax || e is ImplicitArrayCreationExpressionSyntax || call?.Name.Identifier.Text == "ToArray"
                    : call?.Name.Identifier.Text == "ToList" || (e is ObjectCreationExpressionSyntax creation && creation.Type.ToString().StartsWith("List<", StringComparison.Ordinal));
            }

            if (returns.All(OtherShape))
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.CollectionReshaped, invocation.GetLocation(), method.Name,
                    toList ? "an array" : "a list", toList ? "list" : "array"));
            }
        }

        /// <summary>`x ?? false` on a bool? is `x == true` spelled another way.</summary>
        private static void AnalyzeCoalesce(SyntaxNodeAnalysisContext context)
        {
            var coalesce = (BinaryExpressionSyntax)context.Node;
            if (!coalesce.Right.IsKind(SyntaxKind.FalseLiteralExpression) && !coalesce.Right.IsKind(SyntaxKind.TrueLiteralExpression))
            {
                return;
            }

            var info = context.SemanticModel.GetTypeInfo(coalesce.Left, context.CancellationToken);
            var type = (info.Type ?? info.ConvertedType) as INamedTypeSymbol;
            if (type?.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T && type.TypeArguments[0].SpecialType == SpecialType.System_Boolean)
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.NullableBoolCoalesced, coalesce.GetLocation(), coalesce.Left.ToString()));
            }
        }

        /// <summary>An async lambda whose whole body is one awaited call.</summary>
        private static void AnalyzeAsyncLambda(SyntaxNodeAnalysisContext context)
        {
            var lambda = (LambdaExpressionSyntax)context.Node;
            // An event handler (a void delegate) keeps its async: there the await is what observes the task
            var delegateType = context.SemanticModel.GetTypeInfo(lambda, context.CancellationToken).ConvertedType as INamedTypeSymbol;
            if (delegateType?.DelegateInvokeMethod?.ReturnsVoid != false)
            {
                return;
            }

            if (lambda.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword) && lambda.ExpressionBody is AwaitExpressionSyntax awaited &&
                awaited.Expression is InvocationExpressionSyntax)
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.AsyncLambdaOnlyAwaits, lambda.AsyncKeyword.GetLocation()));
            }
        }

        private static readonly Regex Banner = new Regex(@"^//\s*={3,}", RegexOptions.Compiled);

        /// <summary>#region directives and // === banners: organisation by hand.</summary>
        private static void AnalyzeStructureComments(SyntaxTreeAnalysisContext context)
        {
            foreach (var trivia in context.Tree.GetRoot(context.CancellationToken).DescendantTrivia(descendIntoTrivia: true))
            {
                if (trivia.IsKind(SyntaxKind.RegionDirectiveTrivia) || trivia.IsKind(SyntaxKind.EndRegionDirectiveTrivia))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Descriptors.StructureComment, trivia.GetLocation(), trivia.ToString().Trim()));
                }
                else if (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) && Banner.IsMatch(trivia.ToString()))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Descriptors.StructureComment, trivia.GetLocation(), trivia.ToString().Trim()));
                }
            }
        }

        /// <summary>
        ///     In a class built on ObservableObject, a `new RelayCommand(...)` or a property that only wraps
        ///     SetProperty is what [RelayCommand] and [ObservableProperty] generate; the other view models let them.
        /// </summary>
        private static void AnalyzeToolkitShapes(SyntaxNodeAnalysisContext context)
        {
            var type = context.Node.FirstAncestorOrSelf<TypeDeclarationSyntax>();
            if (type == null || !(context.SemanticModel.GetDeclaredSymbol(type, context.CancellationToken) is INamedTypeSymbol symbol) || !IsObservableObject(symbol))
            {
                return;
            }

            if (context.Node is ObjectCreationExpressionSyntax creation)
            {
                var name = creation.Type is GenericNameSyntax generic ? generic.Identifier.Text : (creation.Type as SimpleNameSyntax)?.Identifier.Text;
                if (name != "RelayCommand" && name != "AsyncRelayCommand")
                {
                    return;
                }

                // a command over a virtual method dispatches to overrides, which the attribute cannot
                var target = creation.ArgumentList?.Arguments.FirstOrDefault()?.Expression;
                if (target is IdentifierNameSyntax methodGroup &&
                    context.SemanticModel.GetSymbolInfo(methodGroup, context.CancellationToken).CandidateSymbols.Concat(new[] { context.SemanticModel.GetSymbolInfo(methodGroup, context.CancellationToken).Symbol })
                        .OfType<IMethodSymbol>().Any(m => m.IsVirtual || m.IsAbstract || m.IsOverride))
                {
                    return;
                }

                var assigned = (creation.Parent as AssignmentExpressionSyntax)?.Left.ToString() ?? name;
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.HandBuiltCommand, creation.GetLocation(), assigned));
                return;
            }

            var property = (PropertyDeclarationSyntax)context.Node;
            var getter = property.AccessorList?.Accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
            var setter = property.AccessorList?.Accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.SetAccessorDeclaration));
            if (getter == null || setter == null || property.AccessorList.Accessors.Count != 2)
            {
                return;
            }

            var read = getter.ExpressionBody?.Expression ?? (getter.Body?.Statements.Count == 1 ? (getter.Body.Statements[0] as ReturnStatementSyntax)?.Expression : null);
            var write = setter.ExpressionBody?.Expression ?? (setter.Body?.Statements.Count == 1 ? (setter.Body.Statements[0] as ExpressionStatementSyntax)?.Expression : null);
            if (!(read is IdentifierNameSyntax field) || !(write is InvocationExpressionSyntax invocation) || invocation.Expression.ToString() != "SetProperty" ||
                invocation.ArgumentList.Arguments.Count != 2 || invocation.ArgumentList.Arguments[0].Expression.ToString() != field.Identifier.Text ||
                invocation.ArgumentList.Arguments[1].Expression.ToString() != "value")
            {
                return;
            }

            context.ReportDiagnostic(Diagnostic.Create(Descriptors.HandWrittenObservableProperty, property.Identifier.GetLocation(), property.Identifier.Text));
        }

        private static bool IsObservableObject(INamedTypeSymbol type)
        {
            for (var t = type.BaseType; t != null; t = t.BaseType)
            {
                if (t.Name == "ObservableObject")
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>`x is Derived || x is Base`, or `case Derived: case Base:`: the base type's test already covers the derived one.</summary>
        private static void AnalyzeTypeTests(SyntaxNodeAnalysisContext context)
        {
            var tests = new List<(TypeSyntax Type, string Subject)>();
            if (context.Node is BinaryExpressionSyntax or)
            {
                foreach (var operand in Operands(or))
                {
                    if (operand is BinaryExpressionSyntax isExpression && isExpression.IsKind(SyntaxKind.IsExpression) && isExpression.Right is TypeSyntax type)
                    {
                        tests.Add((type, isExpression.Left.ToString()));
                    }
                    else if (operand is IsPatternExpressionSyntax pattern && pattern.Pattern is TypePatternSyntax typePattern)
                    {
                        tests.Add((typePattern.Type, pattern.Expression.ToString()));
                    }
                    else if (operand is IsPatternExpressionSyntax declaration && declaration.Pattern is DeclarationPatternSyntax declared)
                    {
                        tests.Add((declared.Type, declaration.Expression.ToString()));
                    }
                }
            }
            else if (context.Node is SwitchSectionSyntax section)
            {
                // `case SomeType:` parses as a plain case label whose value names the type
                foreach (var label in section.Labels.OfType<CaseSwitchLabelSyntax>())
                {
                    if (label.Value is TypeSyntax named && context.SemanticModel.GetSymbolInfo(named, context.CancellationToken).Symbol is INamedTypeSymbol)
                    {
                        tests.Add((named, string.Empty));
                    }
                }

                foreach (var label in section.Labels.OfType<CasePatternSwitchLabelSyntax>())
                {
                    if (label.WhenClause == null && label.Pattern is TypePatternSyntax typePattern)
                    {
                        tests.Add((typePattern.Type, string.Empty));
                    }
                    else if (label.WhenClause == null && label.Pattern is DeclarationPatternSyntax declared)
                    {
                        tests.Add((declared.Type, string.Empty));
                    }
                }
            }

            if (tests.Count < 2)
            {
                return;
            }

            var symbols = tests.Select(t => (t.Subject, Symbol: context.SemanticModel.GetTypeInfo(t.Type, context.CancellationToken).Type as INamedTypeSymbol, t.Type)).ToList();
            foreach (var narrow in symbols)
            {
                foreach (var wide in symbols)
                {
                    if (narrow.Symbol == null || wide.Symbol == null || narrow.Subject != wide.Subject ||
                        SymbolEqualityComparer.Default.Equals(narrow.Symbol, wide.Symbol) || !DerivesFrom(narrow.Symbol, wide.Symbol))
                    {
                        continue;
                    }

                    context.ReportDiagnostic(Diagnostic.Create(Descriptors.TypeTestSubsumed, narrow.Type.GetLocation(), narrow.Symbol.Name, wide.Symbol.Name));
                }
            }
        }

        private static IEnumerable<ExpressionSyntax> Operands(BinaryExpressionSyntax or)
        {
            foreach (var side in new[] { or.Left, or.Right })
            {
                var operand = side is ParenthesizedExpressionSyntax parenthesized ? parenthesized.Expression : side;
                if (operand is BinaryExpressionSyntax nested && nested.IsKind(SyntaxKind.LogicalOrExpression))
                {
                    // the outer node reports the whole chain; nested ones would repeat it
                    yield break;
                }

                yield return operand;
            }
        }

        private static bool DerivesFrom(INamedTypeSymbol type, INamedTypeSymbol candidateBase)
        {
            for (var t = type.BaseType; t != null; t = t.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(t, candidateBase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>A blank line before the closing brace of a type, a namespace or a block (Roslynator's RCS1036 left a constructor's).</summary>
        private static void AnalyzeClosingBrace(SyntaxNodeAnalysisContext context)
        {
            var close = context.Node is BaseTypeDeclarationSyntax type ? type.CloseBraceToken
                : context.Node is NamespaceDeclarationSyntax ns ? ns.CloseBraceToken
                : context.Node is BlockSyntax block ? block.CloseBraceToken : default;
            if (close.IsKind(SyntaxKind.None))
            {
                return;
            }

            var text = context.Node.SyntaxTree.GetText(context.CancellationToken);
            var line = text.Lines.GetLineFromPosition(close.SpanStart).LineNumber;
            if (line > 0 && text.Lines[line - 1].ToString().Trim().Length == 0)
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.BlankLineBeforeClosingBrace, close.GetLocation()));
            }
        }

        /// <summary>
        ///     A catch that logs or handles the exception and then throws it on: the caller reports it
        ///     again. A `catch (Exception)` that opens with LogWarning or LogError in a type that has
        ///     ErrorHandler: HandleError written out. A catch whose whole body is `throw;`: a filter
        ///     on the next catch written as a clause.
        /// </summary>
        private static void AnalyzeCatch(SyntaxNodeAnalysisContext context)
        {
            var clause = (CatchClauseSyntax)context.Node;
            bool Logs(StatementSyntax s) => s is ExpressionStatementSyntax es && es.Expression is InvocationExpressionSyntax logged && IsReport(logged);
            if (clause.Declaration?.Type.ToString() == "OperationCanceledException" && clause.Block.Statements.Any(Logs) &&
                clause.Block.Statements.All(s => s is ReturnStatementSyntax || Logs(s)))
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.CancellationLogged, clause.CatchKeyword.GetLocation()));
            }

            if (clause.Block.Statements.Count == 1 && clause.Block.Statements[0] is ThrowStatementSyntax bare && bare.Expression == null)
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.RethrowOnlyCatch, clause.CatchKeyword.GetLocation(),
                    clause.Declaration?.Type.ToString() ?? "Exception"));
            }

            if (clause.Declaration?.Type.ToString() == "Exception" && clause.Block.Statements.FirstOrDefault() is ExpressionStatementSyntax first &&
                first.Expression is InvocationExpressionSyntax logCall && logCall.Expression is MemberAccessExpressionSyntax logAccess &&
                (logAccess.Name.Identifier.Text == "LogWarning" || logAccess.Name.Identifier.Text == "LogError") &&
                logAccess.Expression.ToString().ToLowerInvariant().Contains("logger") &&
                !clause.Block.DescendantNodes().OfType<ThrowStatementSyntax>().Any() && HasErrorHandler(context))
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.CatchLogsByHand, first.GetLocation(), logAccess.Name.Identifier.Text));
            }

            var report = clause.Block.DescendantNodes().OfType<InvocationExpressionSyntax>().FirstOrDefault(IsReport);
            if (report == null)
            {
                return;
            }

            var rethrow = clause.Block.DescendantNodes().OfType<ThrowStatementSyntax>()
                .FirstOrDefault(t => t.SpanStart > report.SpanStart && (t.Expression == null || Rethrows(t, clause)));
            if (rethrow != null)
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.CatchReportsThenRethrows, clause.CatchKeyword.GetLocation()));
            }
        }

        private static bool HasErrorHandler(SyntaxNodeAnalysisContext context)
        {
            for (var type = context.ContainingSymbol?.ContainingType; type != null; type = type.BaseType)
            {
                if (type.GetMembers("ErrorHandler").Length > 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsReport(InvocationExpressionSyntax invocation)
        {
            if (!(invocation.Expression is MemberAccessExpressionSyntax access))
            {
                return false;
            }

            var method = access.Name.Identifier.Text;
            var receiver = access.Expression.ToString();
            if (method == "HandleError" || method == "HandleErrorAsync")
            {
                return receiver.EndsWith("ErrorHandler") || receiver.EndsWith("errorHandler");
            }

            return LogMethod.IsMatch(method) && method != "LogTrace" && receiver.ToLowerInvariant().Contains("logger");
        }

        private static bool Rethrows(ThrowStatementSyntax statement, CatchClauseSyntax clause)
        {
            var variable = clause.Declaration?.Identifier.Text;
            return variable != null && statement.Expression.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Any(i => i.Identifier.Text == variable);
        }

        private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
        {
            var invocation = (InvocationExpressionSyntax)context.Node;
            if (invocation.Expression is MemberBindingExpressionSyntax binding && binding.Name.Identifier.Text == "Any" && invocation.ArgumentList.Arguments.Count == 0)
            {
                CheckAnyOnCounted(context, invocation);
                return;
            }

            var name = (invocation.Expression as MemberAccessExpressionSyntax)?.Name.Identifier.Text
                       ?? (invocation.Expression as IdentifierNameSyntax)?.Identifier.Text;
            if (name == null)
            {
                return;
            }

            if (LogMethod.IsMatch(name))
            {
                CheckLogArguments(context, invocation.ArgumentList);
                return;
            }

            if (name == "ToList" || name == "ToArray")
            {
                CheckReshapedCollection(context, invocation, name);
                return;
            }

            if (name == "AddSingleton" || name == "AddTransient" || name == "AddScoped")
            {
                CheckFactory(context, invocation);
                return;
            }

            if (name == "GetValue" || name == "SetValue" || name == "TryGetValue" || name == "Remove")
            {
                if (!(invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax first) || !first.IsKind(SyntaxKind.StringLiteralExpression))
                {
                    return;
                }

                var receiver = (invocation.Expression as MemberAccessExpressionSyntax)?.Expression;
                var type = receiver == null ? null : context.SemanticModel.GetTypeInfo(receiver).Type;
                if (type != null && type.Name.Contains("Preferences"))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Descriptors.PreferenceKeyLiteral, first.GetLocation()));
                }
            }
        }

        /// <summary>`x?.Any() == true` on a list or array: `x is { Count: > 0 }` says the same without the enumerator.</summary>
        private static void CheckAnyOnCounted(SyntaxNodeAnalysisContext context, InvocationExpressionSyntax invocation)
        {
            var conditional = invocation.FirstAncestorOrSelf<ConditionalAccessExpressionSyntax>(c => c.WhenNotNull == invocation || c.WhenNotNull.DescendantNodesAndSelf().Contains(invocation));
            var receiverType = conditional == null ? null : context.SemanticModel.GetTypeInfo(conditional.Expression, context.CancellationToken).Type;
            if (receiverType == null)
            {
                return;
            }

            var counted = receiverType is IArrayTypeSymbol ? "Length"
                : receiverType.GetMembers("Count").OfType<IPropertySymbol>().Any(p => p.Type.SpecialType == SpecialType.System_Int32) ? "Count"
                : null;
            if (counted != null)
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.AnyThroughConditionalOnCounted, invocation.GetLocation(), conditional.Expression.ToString(), counted));
            }
        }

        /// <summary>
        ///     A registration whose lambda only resolves services (var x = provider.GetRequiredService&lt;T&gt;())
        ///     and returns a constructor call over them: the container does that by itself from the type.
        /// </summary>
        private static void CheckFactory(SyntaxNodeAnalysisContext context, InvocationExpressionSyntax invocation)
        {
            if (!(invocation.ArgumentList.Arguments.LastOrDefault()?.Expression is LambdaExpressionSyntax lambda))
            {
                return;
            }

            var locals = new HashSet<string>();
            ExpressionSyntax result;
            if (lambda.Body is BlockSyntax block)
            {
                var statements = block.Statements;
                if (statements.Count == 0 || !(statements[statements.Count - 1] is ReturnStatementSyntax returns))
                {
                    return;
                }

                foreach (var statement in statements.Take(statements.Count - 1))
                {
                    if (!(statement is LocalDeclarationStatementSyntax declaration) || declaration.Declaration.Variables.Count != 1 ||
                        !IsResolution(declaration.Declaration.Variables[0].Initializer?.Value))
                    {
                        return;
                    }

                    locals.Add(declaration.Declaration.Variables[0].Identifier.Text);
                }

                result = returns.Expression;
            }
            else
            {
                result = lambda.Body as ExpressionSyntax;
            }

            if (!(result is ObjectCreationExpressionSyntax creation) || creation.Initializer != null ||
                creation.ArgumentList?.Arguments.Any(a => !(a.Expression is IdentifierNameSyntax id && locals.Contains(id.Identifier.Text)) && !IsResolution(a.Expression)) == true)
            {
                return;
            }

            context.ReportDiagnostic(Diagnostic.Create(Descriptors.HandWrittenFactory, lambda.GetLocation(), creation.Type.ToString()));
        }

        private static bool IsResolution(ExpressionSyntax expression)
        {
            var name = ((expression as InvocationExpressionSyntax)?.Expression as MemberAccessExpressionSyntax)?.Name.Identifier.Text;
            return name == "GetRequiredService" || name == "GetService";
        }

        private static void AnalyzeObjectCreation(SyntaxNodeAnalysisContext context)
        {
            var creation = (ObjectCreationExpressionSyntax)context.Node;
            if (creation.Type.ToString().EndsWith("Exception") && creation.ArgumentList != null)
            {
                CheckLogArguments(context, creation.ArgumentList, urlsOnly: true);
            }
        }

        private static void CheckLogArguments(SyntaxNodeAnalysisContext context, ArgumentListSyntax arguments, bool urlsOnly = false)
        {
            var text = arguments.ToString();
            if (!urlsOnly)
            {
                var code = Regex.Replace(text, @"\$?@?""(?:[^""\\]|\\.)*""", "\"\"");
                code = Regex.Replace(code, @"\bRedact\w*\(", "(");
                code = Regex.Replace(code, @"IsNullOrEmpty\(\s*[\w.?]+\s*\)", string.Empty);
                var match = Credential.Match(code);
                if (match.Success)
                {
                    context.ReportDiagnostic(Diagnostic.Create(Descriptors.CredentialLogged, arguments.GetLocation(), match.Value));
                }
            }

            if (!text.Contains("RedactApiKey") && !text.Contains("redacted"))
            {
                var match = StreamUrl.Match(text);
                if (match.Success)
                {
                    context.ReportDiagnostic(Diagnostic.Create(Descriptors.StreamUrlLogged, arguments.GetLocation(), match.Value));
                }
            }
        }

        /// <summary>
        ///     `x?.Cancel(); x = new CancellationTokenSource()` is AsyncHelper.Supersede written out;
        ///     `x?.Cancel(); x?.Dispose();` is AsyncHelper.Cancel. Without the `?.` the source is
        ///     always there (a view model's own lifetime source), and cancel-then-dispose is its teardown.
        ///     And a log or HandleError call right before a throw: the exception reports the failure.
        /// </summary>
        private static void AnalyzeBlock(SyntaxNodeAnalysisContext context)
        {
            var block = (BlockSyntax)context.Node;
            AnalyzeConditionalAfterDereference(context, block);
            AnalyzeAdjacentStatements(context, block.Statements);

            if (context.Node.SyntaxTree.FilePath.EndsWith("AsyncHelper.cs"))
            {
                return;
            }

            for (var i = 0; i + 1 < block.Statements.Count; i++)
            {
                if (block.Statements[i] is ExpressionStatementSyntax reported && block.Statements[i + 1] is ThrowStatementSyntax &&
                    reported.Expression is InvocationExpressionSyntax reportCall && IsReport(reportCall))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Descriptors.LogThenThrow, reported.GetLocation(),
                        ((MemberAccessExpressionSyntax)reportCall.Expression).Name.Identifier.Text));
                }

                if (!(block.Statements[i] is ExpressionStatementSyntax cancel) || !(block.Statements[i + 1] is ExpressionStatementSyntax replace))
                {
                    continue;
                }

                var cancelText = cancel.Expression.ToString();
                var m = Regex.Match(cancelText, @"^([\w.]+)\s*\??\.Cancel\(\)$");
                if (!m.Success)
                {
                    continue;
                }

                if (replace.Expression is AssignmentExpressionSyntax assignment && assignment.Left.ToString().TrimStart() == m.Groups[1].Value &&
                    assignment.Right is ObjectCreationExpressionSyntax creation && creation.Type.ToString().EndsWith("CancellationTokenSource"))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Descriptors.TokenSourceReplacedByHand, cancel.GetLocation(), m.Groups[1].Value, "replaced", "Supersede"));
                }
                else if (cancelText.Contains("?.") && Regex.IsMatch(replace.Expression.ToString(), "^" + Regex.Escape(m.Groups[1].Value) + @"\s*\??\.Dispose\(\)$"))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Descriptors.TokenSourceReplacedByHand, cancel.GetLocation(), m.Groups[1].Value, "disposed", "Cancel"));
                }
            }
        }

        /// <summary>
        ///     Statement by statement: what a statement dereferences unconditionally (outside lambdas, the
        ///     right of &amp;&amp;, || and ??, the branches of ?: and the tail of a ?.) is not null for the
        ///     statements after it, until something assigns it; a ?. on it there is redundant.
        /// </summary>
        private static void AnalyzeConditionalAfterDereference(SyntaxNodeAnalysisContext context, BlockSyntax block)
        {
            var dereferenced = new HashSet<string>(StringComparer.Ordinal);
            foreach (var statement in block.Statements)
            {
                if (dereferenced.Count > 0)
                {
                    foreach (var conditional in statement.DescendantNodes(n => !IsFunction(n)).OfType<ConditionalAccessExpressionSyntax>())
                    {
                        var chain = ChainText(conditional.Expression);
                        if (chain != null && dereferenced.Contains(chain))
                        {
                            context.ReportDiagnostic(Diagnostic.Create(Descriptors.ConditionalAfterDereference, conditional.OperatorToken.GetLocation(), chain));
                        }
                    }

                    foreach (var written in statement.DescendantNodes().Select(WrittenChain).Where(w => w != null).ToList())
                    {
                        dereferenced.RemoveWhere(c => c == written || c.StartsWith(written + ".", StringComparison.Ordinal));
                    }
                }

                foreach (var expression in UnconditionalExpressions(statement))
                {
                    CollectDereferences(expression, dereferenced);
                }
            }
        }

        private static bool IsFunction(SyntaxNode node)
        {
            return node is AnonymousFunctionExpressionSyntax || node is LocalFunctionStatementSyntax;
        }

        private static string WrittenChain(SyntaxNode node)
        {
            switch (node)
            {
                case AssignmentExpressionSyntax assignment:
                    return ChainText(assignment.Left) ?? assignment.Left.ToString();
                case ArgumentSyntax argument when !argument.RefOrOutKeyword.IsKind(SyntaxKind.None):
                    return ChainText(argument.Expression) ?? argument.Expression.ToString();
                case DeclarationExpressionSyntax declaration:
                    return declaration.Designation.ToString();
                default:
                    return null;
            }
        }

        private static IEnumerable<ExpressionSyntax> UnconditionalExpressions(StatementSyntax statement)
        {
            switch (statement)
            {
                case ExpressionStatementSyntax expression:
                    yield return expression.Expression;
                    break;
                case LocalDeclarationStatementSyntax local:
                    foreach (var variable in local.Declaration.Variables)
                    {
                        if (variable.Initializer != null)
                        {
                            yield return variable.Initializer.Value;
                        }
                    }

                    break;
                case ReturnStatementSyntax ret when ret.Expression != null:
                    yield return ret.Expression;
                    break;
                case ThrowStatementSyntax thr when thr.Expression != null:
                    yield return thr.Expression;
                    break;
                case IfStatementSyntax ifs:
                    yield return ifs.Condition;
                    break;
                case WhileStatementSyntax wh:
                    yield return wh.Condition;
                    break;
                case ForEachStatementSyntax fe:
                    yield return fe.Expression;
                    break;
                case SwitchStatementSyntax sw:
                    yield return sw.Expression;
                    break;
                case LockStatementSyntax lk:
                    yield return lk.Expression;
                    break;
                case UsingStatementSyntax us when us.Expression != null:
                    yield return us.Expression;
                    break;
            }
        }

        private static void CollectDereferences(SyntaxNode node, HashSet<string> into)
        {
            switch (node)
            {
                case null:
                    return;
                case AnonymousFunctionExpressionSyntax:
                    return;
                case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.LogicalAndExpression) || binary.IsKind(SyntaxKind.LogicalOrExpression) ||
                                                        binary.IsKind(SyntaxKind.CoalesceExpression):
                    CollectDereferences(binary.Left, into);
                    return;
                case ConditionalExpressionSyntax conditional:
                    CollectDereferences(conditional.Condition, into);
                    return;
                case ConditionalAccessExpressionSyntax access:
                    CollectDereferences(access.Expression, into);
                    return;
                case MemberAccessExpressionSyntax member when member.IsKind(SyntaxKind.SimpleMemberAccessExpression):
                    for (var chain = ChainText(member.Expression); chain != null;)
                    {
                        into.Add(chain);
                        var dot = chain.LastIndexOf('.');
                        chain = dot < 0 ? null : chain.Substring(0, dot);
                    }

                    break;
                case ElementAccessExpressionSyntax element:
                    var target = ChainText(element.Expression);
                    if (target != null)
                    {
                        into.Add(target);
                    }

                    break;
            }

            foreach (var child in node.ChildNodes())
            {
                CollectDereferences(child, into);
            }
        }

        /// <summary>`a`, `this.a`, `a.b.c`: names and plain member accesses only, else null.</summary>
        private static string ChainText(ExpressionSyntax expression)
        {
            switch (expression)
            {
                case IdentifierNameSyntax id:
                    return id.Identifier.Text;
                case ThisExpressionSyntax:
                    return "this";
                case BaseExpressionSyntax:
                    return "base";
                case ParenthesizedExpressionSyntax parenthesized:
                    return ChainText(parenthesized.Expression);
                case MemberAccessExpressionSyntax member when member.IsKind(SyntaxKind.SimpleMemberAccessExpression):
                    var left = ChainText(member.Expression);
                    return left == null ? null : left + "." + member.Name.Identifier.Text;
                default:
                    return null;
            }
        }

        /// <summary>System, then Windows, then the rest, each alphabetical; aliases and static usings sit last and are not ordered.</summary>
        private static void AnalyzeUsings(SyntaxTreeAnalysisContext context)
        {
            var usings = context.Tree.GetRoot(context.CancellationToken).DescendantNodes().OfType<UsingDirectiveSyntax>()
                .Where(u => u.Alias == null && u.StaticKeyword.IsKind(SyntaxKind.None)).ToList();
            for (var i = 0; i + 1 < usings.Count; i++)
            {
                var first = UsingKey(usings[i]);
                var second = UsingKey(usings[i + 1]);
                if (first.Group > second.Group || (first.Group == second.Group && string.CompareOrdinal(first.Name, second.Name) > 0))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Descriptors.UsingOutOfOrder, usings[i + 1].GetLocation(),
                        usings[i + 1].Name.ToString(), usings[i].Name.ToString()));
                    return;
                }
            }
        }

        private static (int Group, string Name) UsingKey(UsingDirectiveSyntax directive)
        {
            var name = directive.Name.ToString();
            var group = name == "System" || name.StartsWith("System.", StringComparison.Ordinal) ? 0
                : name == "Windows" || name.StartsWith("Windows.", StringComparison.Ordinal) ? 1 : 2;
            return (group, name.ToLowerInvariant());
        }

        /// <summary>A switch section's statements are a list without a block; the adjacent-statement rules read them too.</summary>
        private static void AnalyzeSwitchSection(SyntaxNodeAnalysisContext context)
        {
            AnalyzeAdjacentStatements(context, ((SwitchSectionSyntax)context.Node).Statements);
        }

        /// <summary>Two if statements in a row with one body (GEL050); two log calls in a row at one level (GEL052).</summary>
        private static void AnalyzeAdjacentStatements(SyntaxNodeAnalysisContext context, SyntaxList<StatementSyntax> statements)
        {
            var crashHandler = context.Node.SyntaxTree.FilePath.EndsWith("App.xaml.cs");
            for (var i = 0; i + 1 < statements.Count; i++)
            {
                if (statements[i] is IfStatementSyntax first && first.Else == null &&
                    statements[i + 1] is IfStatementSyntax second && second.Else == null &&
                    SyntaxFactory.AreEquivalent(first.Statement, second.Statement, false))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Descriptors.AdjacentIfsOneBody, second.IfKeyword.GetLocation()));
                }

                var level = LogCallName(statements[i]);
                if (!crashHandler && level != null && level == LogCallName(statements[i + 1]))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Descriptors.ConsecutiveLogCalls, statements[i + 1].GetLocation(), level));
                }
            }
        }

        /// <summary>`receiver.LogLevel(` as the whole statement: the level's method name, else null.</summary>
        private static string LogCallName(StatementSyntax statement)
        {
            if (statement is ExpressionStatementSyntax expression && expression.Expression is InvocationExpressionSyntax invocation &&
                invocation.Expression is MemberAccessExpressionSyntax access && access.Name.Identifier.Text.StartsWith("Log", StringComparison.Ordinal) &&
                access.Name.Identifier.Text != "Log" && Regex.IsMatch(access.Expression.ToString(), @"^_?[Ll]ogger$"))
            {
                return access.Name.Identifier.Text;
            }

            return null;
        }

        /// <summary>
        ///     `_ = SomeTask` where FireAndForget is at hand: in a type that inherits it, or a Task.Run whose
        ///     lambda does not catch for itself (AsyncHelper.FireAndForget takes any logger).
        /// </summary>
        private static void AnalyzeDiscardedTask(SyntaxNodeAnalysisContext context)
        {
            var assignment = (AssignmentExpressionSyntax)context.Node;
            if (!(assignment.Left is IdentifierNameSyntax discard) || discard.Identifier.Text != "_" ||
                context.Node.SyntaxTree.FilePath.EndsWith("AsyncHelper.cs"))
            {
                return;
            }

            var type = context.SemanticModel.GetTypeInfo(assignment.Right, context.CancellationToken).Type;
            if (type == null || type.Name != "Task" || type.ContainingNamespace?.ToDisplayString() != "System.Threading.Tasks")
            {
                return;
            }

            if (assignment.Right is InvocationExpressionSyntax run && run.Expression.ToString() == "Task.Run" &&
                run.ArgumentList.Arguments.FirstOrDefault()?.Expression is AnonymousFunctionExpressionSyntax lambda &&
                !lambda.DescendantNodes().OfType<TryStatementSyntax>().Any())
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.TaskDiscardedWithFireAndForget, assignment.GetLocation()));
                return;
            }

            var declaration = assignment.FirstAncestorOrSelf<TypeDeclarationSyntax>();
            var symbol = declaration == null ? null : context.SemanticModel.GetDeclaredSymbol(declaration, context.CancellationToken);
            for (var t = symbol?.BaseType; t != null; t = t.BaseType)
            {
                if (t.GetMembers("FireAndForget").Any())
                {
                    context.ReportDiagnostic(Diagnostic.Create(Descriptors.TaskDiscardedWithFireAndForget, assignment.GetLocation()));
                    return;
                }
            }
        }

        /// <summary>A page's `new X ViewModel { get; }` stores a view model BasePage never sees.</summary>
        private static void AnalyzeProperty(SyntaxNodeAnalysisContext context)
        {
            var property = (PropertyDeclarationSyntax)context.Node;
            if (property.Identifier.Text != "ViewModel" || !property.Modifiers.Any(SyntaxKind.NewKeyword) || property.AccessorList == null)
            {
                return;
            }

            for (var type = context.ContainingSymbol?.ContainingType; type != null; type = type.BaseType)
            {
                if (type.Name == "BasePage")
                {
                    context.ReportDiagnostic(Diagnostic.Create(Descriptors.PageViewModelOutsideBase, property.Identifier.GetLocation()));
                    return;
                }
            }
        }

        /// <summary>A /// comment followed by a blank line or a closing brace documents nothing.</summary>
        private static void AnalyzeDocComments(SyntaxTreeAnalysisContext context)
        {
            var root = context.Tree.GetRoot(context.CancellationToken);
            foreach (var token in root.DescendantTokens())
            {
                var trivia = token.LeadingTrivia;
                for (var i = 0; i < trivia.Count; i++)
                {
                    if (!trivia[i].IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia))
                    {
                        continue;
                    }

                    var orphan = token.IsKind(SyntaxKind.CloseBraceToken) || token.IsKind(SyntaxKind.EndOfFileToken);
                    for (var j = i + 1; j < trivia.Count && !orphan; j++)
                    {
                        if (trivia[j].IsKind(SyntaxKind.WhitespaceTrivia))
                        {
                            continue;
                        }

                        orphan = trivia[j].IsKind(SyntaxKind.EndOfLineTrivia);
                        break;
                    }

                    if (orphan)
                    {
                        context.ReportDiagnostic(Diagnostic.Create(Descriptors.OrphanedDocComment, trivia[i].GetLocation()));
                    }
                }
            }
        }
    }
}
