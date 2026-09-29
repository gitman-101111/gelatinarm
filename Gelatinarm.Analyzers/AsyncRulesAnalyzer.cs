using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Gelatinarm.Analyzers
{
    /// <summary>
    ///     Async shapes the compiler here does not report: an async body that never awaits (the
    ///     compiler in Visual Studio 18 emits no CS1998), a Task method that is synchronous in all
    ///     but signature, a synchronous wait on a task, and an async lambda handed to a delegate
    ///     that returns void.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class AsyncRulesAnalyzer : DiagnosticAnalyzer
    {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(
            Descriptors.AsyncWithoutAwait, Descriptors.TaskMethodNeverWaits, Descriptors.SyncOverAsync,
            Descriptors.AsyncLambdaToVoidDelegate);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(AnalyzeMethod, SyntaxKind.MethodDeclaration);
            context.RegisterSyntaxNodeAction(AnalyzeLocalFunction, SyntaxKind.LocalFunctionStatement);
            context.RegisterSyntaxNodeAction(AnalyzeLambda, SyntaxKind.ParenthesizedLambdaExpression,
                SyntaxKind.SimpleLambdaExpression, SyntaxKind.AnonymousMethodExpression);
            context.RegisterSyntaxNodeAction(AnalyzeMemberAccess, SyntaxKind.SimpleMemberAccessExpression);
        }

        private static void AnalyzeMethod(SyntaxNodeAnalysisContext context)
        {
            var method = (MethodDeclarationSyntax)context.Node;
            var body = (SyntaxNode)method.Body ?? method.ExpressionBody;
            if (body == null)
            {
                return;
            }

            if (method.Modifiers.Any(SyntaxKind.AsyncKeyword))
            {
                if (!Awaits(body))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Descriptors.AsyncWithoutAwait, method.Identifier.GetLocation(), method.Identifier.Text));
                }

                return;
            }

            // Task-returning, not async: every return is Task.CompletedTask
            if (method.Body == null || method.Modifiers.Any(m => m.IsKind(SyntaxKind.OverrideKeyword) || m.IsKind(SyntaxKind.VirtualKeyword) || m.IsKind(SyntaxKind.AbstractKeyword)))
            {
                return;
            }

            if (!(method.ReturnType is IdentifierNameSyntax returnType) || returnType.Identifier.Text != "Task")
            {
                return;
            }

            var returns = method.Body.DescendantNodes(n => !(n is AnonymousFunctionExpressionSyntax) && !(n is LocalFunctionStatementSyntax))
                .OfType<ReturnStatementSyntax>().ToList();
            if (returns.Count == 0 || !returns.All(r => r.Expression?.ToString() == "Task.CompletedTask"))
            {
                return;
            }

            var symbol = context.SemanticModel.GetDeclaredSymbol(method);
            if (symbol == null || ImplementsInterfaceMember(symbol))
            {
                return;
            }

            context.ReportDiagnostic(Diagnostic.Create(Descriptors.TaskMethodNeverWaits, method.Identifier.GetLocation(), method.Identifier.Text));
        }

        private static bool ImplementsInterfaceMember(IMethodSymbol method)
        {
            return method.ContainingType.AllInterfaces
                .SelectMany(i => i.GetMembers().OfType<IMethodSymbol>())
                .Any(m => SymbolEqualityComparer.Default.Equals(method.ContainingType.FindImplementationForInterfaceMember(m), method));
        }

        private static void AnalyzeLocalFunction(SyntaxNodeAnalysisContext context)
        {
            var function = (LocalFunctionStatementSyntax)context.Node;
            var body = (SyntaxNode)function.Body ?? function.ExpressionBody;
            if (body != null && function.Modifiers.Any(SyntaxKind.AsyncKeyword) && !Awaits(body))
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.AsyncWithoutAwait, function.Identifier.GetLocation(), function.Identifier.Text));
            }
        }

        private static void AnalyzeLambda(SyntaxNodeAnalysisContext context)
        {
            var lambda = (AnonymousFunctionExpressionSyntax)context.Node;
            if (lambda.AsyncKeyword.IsKind(SyntaxKind.None) || lambda.Body == null)
            {
                return;
            }

            if (!Awaits(lambda.Body))
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.AsyncWithoutAwait, lambda.AsyncKeyword.GetLocation(), "lambda"));
            }

            // an event subscription is the one place async void belongs; a body that completes a
            // TaskCompletionSource is awaited through it (UiHelper's dispatcher hop)
            if ((lambda.Parent is AssignmentExpressionSyntax assignment && assignment.IsKind(SyntaxKind.AddAssignmentExpression)) ||
                lambda.Body.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(CompletesTaskSource))
            {
                return;
            }

            var delegateType = context.SemanticModel.GetTypeInfo(lambda, context.CancellationToken).ConvertedType as INamedTypeSymbol;
            if (delegateType?.DelegateInvokeMethod?.ReturnsVoid == true)
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.AsyncLambdaToVoidDelegate, lambda.AsyncKeyword.GetLocation(),
                    delegateType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
            }
        }

        private static bool CompletesTaskSource(InvocationExpressionSyntax invocation)
        {
            var name = (invocation.Expression as MemberAccessExpressionSyntax)?.Name.Identifier.Text;
            return name != null && (name.EndsWith("SetResult") || name.EndsWith("SetException") || name.EndsWith("SetCanceled"));
        }

        /// <summary>An await anywhere in the body, nested lambdas and local functions excluded: their awaits are their own.</summary>
        private static bool Awaits(SyntaxNode body)
        {
            return body.DescendantNodesAndSelf(n => !(n is AnonymousFunctionExpressionSyntax && n != body) && !(n is LocalFunctionStatementSyntax && n != body))
                .Any(n => n is AwaitExpressionSyntax ||
                          (n is ForEachStatementSyntax fe && !fe.AwaitKeyword.IsKind(SyntaxKind.None)) ||
                          (n is UsingStatementSyntax us && !us.AwaitKeyword.IsKind(SyntaxKind.None)) ||
                          (n is LocalDeclarationStatementSyntax ld && !ld.AwaitKeyword.IsKind(SyntaxKind.None)));
        }

        private static void AnalyzeMemberAccess(SyntaxNodeAnalysisContext context)
        {
            var access = (MemberAccessExpressionSyntax)context.Node;
            var name = access.Name.Identifier.Text;
            if (name != "Result" && name != "Wait" && name != "GetResult")
            {
                return;
            }

            if (name == "Wait" && !(access.Parent is InvocationExpressionSyntax))
            {
                return;
            }

            var receiver = name == "GetResult"
                ? (access.Expression as InvocationExpressionSyntax)?.Expression is MemberAccessExpressionSyntax awaiter && awaiter.Name.Identifier.Text == "GetAwaiter"
                    ? awaiter.Expression
                    : null
                : access.Expression;
            if (receiver == null)
            {
                return;
            }

            var type = context.SemanticModel.GetTypeInfo(receiver).Type;
            if (type == null || !IsTask(type))
            {
                return;
            }

            context.ReportDiagnostic(Diagnostic.Create(Descriptors.SyncOverAsync, access.Name.GetLocation(), access.ToString()));
        }

        private static bool IsTask(ITypeSymbol type)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                if (t.ContainingNamespace?.ToDisplayString() == "System.Threading.Tasks" && (t.Name == "Task" || t.Name == "ValueTask"))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
