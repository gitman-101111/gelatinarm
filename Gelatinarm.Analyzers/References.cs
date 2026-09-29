using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Gelatinarm.Analyzers
{
    /// <summary>One use of a member: where from, and how.</summary>
    internal sealed class Reference
    {
        public INamedTypeSymbol FromType;
        public IMethodSymbol FromMethod;
        public SyntaxNode Node;
        public bool IsRead;
        public bool IsWrite;
        public bool IsNameOf;
        public bool IsNullCheck;          // == null, != null, is null, is not null
        public bool IsConditionalAccess;  // x?.
        public bool IsSubscription;       // event += handler
        public bool IsInvocation;
        public bool IsXamlGenerated;      // the XAML compiler's .g.cs: x:Bind, event wiring, x:Name fields
        public bool IsToolkitGenerated;   // CommunityToolkit.Mvvm's generated properties and commands
        public ExpressionSyntax AssignedFrom; // for a write: the right-hand side

        public bool IsGenerated => IsXamlGenerated || IsToolkitGenerated;
    }

    /// <summary>
    ///     Every reference to every member of this assembly, resolved by symbol: what the regex scans
    ///     could never do (overloads share a name, same-named members on other types, platform
    ///     types). Built once per compilation and shared by the rules.
    /// </summary>
    internal sealed class References
    {
        private static readonly HashSet<string> CollectionMutators = new HashSet<string>
        {
            "Add", "AddRange", "Clear", "Insert", "Remove", "RemoveAt", "ReplaceAll", "TryAdd", "TryRemove"
        };

        private readonly Dictionary<ISymbol, List<Reference>> _byMember = new Dictionary<ISymbol, List<Reference>>(SymbolEqualityComparer.Default);
        private readonly List<SyntaxTree> _sourceTrees = new List<SyntaxTree>();

        public Compilation Compilation { get; }
        public IReadOnlyList<SyntaxTree> SourceTrees => _sourceTrees;
        public HashSet<string> StringLiterals { get; } = new HashSet<string>(StringComparer.Ordinal);

        public References(Compilation compilation)
        {
            Compilation = compilation;
            foreach (var tree in compilation.SyntaxTrees)
            {
                var xamlGenerated = IsXamlGenerated(tree);
                var toolkitGenerated = IsToolkitGenerated(tree);
                if (!xamlGenerated && !toolkitGenerated)
                {
                    _sourceTrees.Add(tree);
                }

                var model = compilation.GetSemanticModel(tree);
                var root = tree.GetRoot();
                foreach (var token in root.DescendantTokens())
                {
                    if (token.IsKind(SyntaxKind.StringLiteralToken) && !xamlGenerated && !toolkitGenerated)
                    {
                        StringLiterals.Add(token.ValueText);
                    }
                }

                foreach (var name in root.DescendantNodes().OfType<SimpleNameSyntax>())
                {
                    Record(model, name, xamlGenerated, toolkitGenerated);
                }
            }
        }

        public static bool IsXamlGenerated(SyntaxTree tree)
        {
            var path = tree.FilePath ?? string.Empty;
            return path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsToolkitGenerated(SyntaxTree tree)
        {
            var path = tree.FilePath ?? string.Empty;
            return path.IndexOf("CommunityToolkit.Mvvm", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   path.IndexOf("SourceGenerator", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public IReadOnlyList<Reference> Of(ISymbol member)
        {
            return _byMember.TryGetValue(Canonical(member), out var list) ? list : Array.Empty<Reference>();
        }

        /// <summary>The symbol a reference is filed under: the original definition, an extension method's unreduced form.</summary>
        public static ISymbol Canonical(ISymbol symbol)
        {
            if (symbol is IMethodSymbol method && method.ReducedFrom != null)
            {
                symbol = method.ReducedFrom;
            }

            return symbol.OriginalDefinition;
        }

        private void Record(SemanticModel model, SimpleNameSyntax name, bool xamlGenerated, bool toolkitGenerated)
        {
            var info = model.GetSymbolInfo(name);
            var symbol = info.Symbol ?? (info.CandidateSymbols.Length == 1 ? info.CandidateSymbols[0] : null);
            if (symbol == null)
            {
                return;
            }

            if (symbol is IMethodSymbol m && (m.MethodKind == MethodKind.Constructor || m.MethodKind == MethodKind.PropertyGet ||
                                              m.MethodKind == MethodKind.PropertySet || m.MethodKind == MethodKind.EventAdd ||
                                              m.MethodKind == MethodKind.EventRemove))
            {
                return;
            }

            if (!(symbol is IFieldSymbol) && !(symbol is IPropertySymbol) && !(symbol is IEventSymbol) && !(symbol is IMethodSymbol))
            {
                return;
            }

            if (!SymbolEqualityComparer.Default.Equals(symbol.ContainingAssembly, Compilation.Assembly))
            {
                return;
            }

            var expression = Expression(name);
            var reference = new Reference
            {
                Node = expression,
                IsXamlGenerated = xamlGenerated,
                IsToolkitGenerated = toolkitGenerated
            };

            var enclosing = model.GetEnclosingSymbol(name.SpanStart);
            reference.FromMethod = enclosing as IMethodSymbol ?? enclosing?.ContainingSymbol as IMethodSymbol;
            for (var s = enclosing; s != null; s = s.ContainingSymbol)
            {
                if (s is INamedTypeSymbol type)
                {
                    reference.FromType = type;
                    break;
                }
            }

            Classify(expression, reference, symbol);
            var key = Canonical(symbol);
            if (!_byMember.TryGetValue(key, out var list))
            {
                _byMember[key] = list = new List<Reference>();
            }

            list.Add(reference);
        }

        /// <summary>The expression that names the member: `Name`, `x.Name`, `x?.Name` or `Name&lt;T&gt;`.</summary>
        private static ExpressionSyntax Expression(SimpleNameSyntax name)
        {
            if (name.Parent is MemberAccessExpressionSyntax access && access.Name == name)
            {
                return access;
            }

            if (name.Parent is MemberBindingExpressionSyntax binding)
            {
                // x?.Name: the conditional access is the expression
                SyntaxNode node = binding;
                while (node.Parent != null && !(node.Parent is ConditionalAccessExpressionSyntax))
                {
                    node = node.Parent;
                }

                return node.Parent as ExpressionSyntax ?? binding;
            }

            return name;
        }

        private static void Classify(ExpressionSyntax expression, Reference reference, ISymbol symbol)
        {
            var parent = expression.Parent;

            for (var node = expression.Parent; node != null; node = node.Parent)
            {
                if (node is InvocationExpressionSyntax invocation && invocation.Expression is IdentifierNameSyntax id && id.Identifier.Text == "nameof")
                {
                    reference.IsNameOf = true;
                    return;
                }

                if (node is StatementSyntax || node is MemberDeclarationSyntax)
                {
                    break;
                }
            }

            reference.IsInvocation = parent is InvocationExpressionSyntax inv && inv.Expression == expression;

            if (parent is AssignmentExpressionSyntax assignment && assignment.Left == expression)
            {
                reference.IsWrite = true;
                reference.IsRead = !assignment.IsKind(SyntaxKind.SimpleAssignmentExpression);
                reference.AssignedFrom = assignment.Right;
                if (symbol is IEventSymbol && assignment.IsKind(SyntaxKind.AddAssignmentExpression))
                {
                    reference.IsSubscription = true;
                }

                return;
            }

            // (A, B) = ...: each name in the tuple is assigned
            var tuple = parent;
            while (tuple is ArgumentSyntax || tuple is TupleExpressionSyntax)
            {
                if (tuple is TupleExpressionSyntax && tuple.Parent is AssignmentExpressionSyntax deconstruction && deconstruction.Left == tuple)
                {
                    reference.IsWrite = true;
                    return;
                }

                tuple = tuple.Parent;
            }

            if ((parent is PrefixUnaryExpressionSyntax pre && (pre.IsKind(SyntaxKind.PreIncrementExpression) || pre.IsKind(SyntaxKind.PreDecrementExpression))) ||
                (parent is PostfixUnaryExpressionSyntax post && (post.IsKind(SyntaxKind.PostIncrementExpression) || post.IsKind(SyntaxKind.PostDecrementExpression))))
            {
                reference.IsRead = reference.IsWrite = true;
                return;
            }

            if (parent is ArgumentSyntax argument && !argument.RefOrOutKeyword.IsKind(SyntaxKind.None))
            {
                reference.IsRead = reference.IsWrite = true;
                return;
            }

            if (parent is MemberAccessExpressionSyntax access && access.Expression == expression &&
                access.Parent is InvocationExpressionSyntax && CollectionMutators.Contains(access.Name.Identifier.Text))
            {
                reference.IsWrite = true;
                return;
            }

            reference.IsRead = true;
            if (parent is BinaryExpressionSyntax binary && (binary.IsKind(SyntaxKind.EqualsExpression) || binary.IsKind(SyntaxKind.NotEqualsExpression)))
            {
                var other = binary.Left == expression ? binary.Right : binary.Left;
                reference.IsNullCheck = other.IsKind(SyntaxKind.NullLiteralExpression);
            }
            else if (parent is IsPatternExpressionSyntax isPattern && isPattern.Expression == expression)
            {
                var pattern = isPattern.Pattern is UnaryPatternSyntax not ? not.Pattern : isPattern.Pattern;
                reference.IsNullCheck = pattern is ConstantPatternSyntax constant && constant.Expression.IsKind(SyntaxKind.NullLiteralExpression);
            }
            else if (parent is ConditionalAccessExpressionSyntax conditional && conditional.Expression == expression)
            {
                reference.IsConditionalAccess = true;
            }
        }
    }
}
