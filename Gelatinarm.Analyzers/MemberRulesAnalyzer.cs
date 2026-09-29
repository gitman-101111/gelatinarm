using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.RegularExpressions;
using Gelatinarm.Analyzers.Xaml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace Gelatinarm.Analyzers
{
    /// <summary>
    ///     The member rules: who uses what, by symbol, with the XAML's words as the other source of
    ///     references. In a single-assembly app "public" says nothing about reach, so a public member
    ///     nothing references is dead, and one only its own type uses should be private.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class MemberRulesAnalyzer : DiagnosticAnalyzer
    {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(
            Descriptors.MemberNeverReferenced, Descriptors.MemberOnlyUsedInside, Descriptors.MemberWrittenNeverRead,
            Descriptors.ObservableStateNeverSet, Descriptors.FieldOnlyNullChecked,
            Descriptors.InterfaceMemberOnlyImplementationUses, Descriptors.EmptyVirtualHookNeverOverridden,
            Descriptors.StateRedeclaredFromBase, Descriptors.NullCheckOnTrustedField,
            Descriptors.OptionalParameterNeverPassed, Descriptors.OptionalParameterAlwaysPassed,
            Descriptors.PreferenceKeyOneSided, Descriptors.ExceptionMessageRuleOrphaned,
            Descriptors.RdXmlTypeMissing, Descriptors.ShadowState, Descriptors.EnumMemberNeverProduced, Descriptors.AssemblyVersionDiffers);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationAction(Analyze);
        }

        private static void Analyze(CompilationAnalysisContext context)
        {
            var compilation = context.Compilation;
            var references = new References(compilation);
            var xaml = context.Options.AdditionalFiles
                .Where(f => f.Path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                .Select(XamlFile.TryLoad)
                .Where(f => f != null)
                .ToList();
            var xamlWords = new HashSet<string>(xaml.SelectMany(f => f.Words), StringComparer.Ordinal);
            var xamlText = string.Join("\n", xaml.Select(f => f.RawText));
            var rdXml = context.Options.AdditionalFiles.Where(f => f.Path.EndsWith(".rd.xml", StringComparison.OrdinalIgnoreCase)).ToList();

            var types = SourceTypes(compilation.Assembly.GlobalNamespace).ToList();
            var overrides = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            foreach (var type in types)
            {
                foreach (var member in type.GetMembers())
                {
                    var overridden = (member as IMethodSymbol)?.OverriddenMethod ?? (ISymbol)(member as IPropertySymbol)?.OverriddenProperty;
                    if (overridden != null)
                    {
                        overrides.Add(overridden.OriginalDefinition);
                    }
                }
            }

            var walker = new Rules(context, references, xamlWords, xamlText, overrides, types);
            foreach (var type in types)
            {
                walker.Check(type);
            }

            walker.CheckOptionalParameters();
            walker.CheckPreferenceKeys();
            walker.CheckExceptionMessageFilters();
            walker.CheckRdXml(rdXml);
            walker.CheckShadowState();
            CheckAssemblyVersion(context);
        }

        /// <summary>AssemblyVersion and AssemblyFileVersion against the package manifest's Identity Version.</summary>
        private static void CheckAssemblyVersion(CompilationAnalysisContext context)
        {
            var manifest = context.Options.AdditionalFiles.FirstOrDefault(f => f.Path.EndsWith("Package.appxmanifest", StringComparison.OrdinalIgnoreCase))?.GetText()?.ToString();
            var packageVersion = manifest == null ? null : Regex.Match(manifest, "<Identity[^>]*\\sVersion=\"([\\d.]+)\"").Groups[1].Value;
            if (string.IsNullOrEmpty(packageVersion))
            {
                return;
            }

            foreach (var attribute in context.Compilation.Assembly.GetAttributes())
            {
                var name = attribute.AttributeClass?.Name;
                if ((name != "AssemblyVersionAttribute" && name != "AssemblyFileVersionAttribute") || attribute.ConstructorArguments.Length != 1)
                {
                    continue;
                }

                var version = attribute.ConstructorArguments[0].Value as string;
                var location = attribute.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation();
                if (version != packageVersion && location != null)
                {
                    context.ReportDiagnostic(Diagnostic.Create(Descriptors.AssemblyVersionDiffers, location, name.Replace("Attribute", string.Empty), version, packageVersion));
                }
            }
        }

        private static IEnumerable<INamedTypeSymbol> SourceTypes(INamespaceSymbol ns)
        {
            foreach (var type in ns.GetTypeMembers())
            {
                foreach (var t in WithNested(type))
                {
                    yield return t;
                }
            }

            foreach (var child in ns.GetNamespaceMembers())
            {
                foreach (var t in SourceTypes(child))
                {
                    yield return t;
                }
            }
        }

        private static IEnumerable<INamedTypeSymbol> WithNested(INamedTypeSymbol type)
        {
            yield return type;
            foreach (var nested in type.GetTypeMembers())
            {
                foreach (var t in WithNested(nested))
                {
                    yield return t;
                }
            }
        }

        private sealed class Rules
        {
            private static readonly HashSet<string> ShadowExcludedTypes = new HashSet<string>
            {
                "CancellationTokenSource", "DispatcherTimer", "Timer", "SemaphoreSlim", "Task", "ILogger", "EventHandler"
            };

            private readonly CompilationAnalysisContext _context;
            private readonly References _refs;
            private readonly HashSet<string> _xamlWords;
            private readonly string _xamlText;
            private readonly HashSet<ISymbol> _overrides;
            private readonly List<INamedTypeSymbol> _types;
            private readonly List<(IMethodSymbol Method, IParameterSymbol Parameter)> _optional = new List<(IMethodSymbol, IParameterSymbol)>();
            private readonly List<IFieldSymbol> _preferenceKeys = new List<IFieldSymbol>();
            private readonly Dictionary<string, List<IFieldSymbol>> _shadowState = new Dictionary<string, List<IFieldSymbol>>(StringComparer.OrdinalIgnoreCase);

            public Rules(CompilationAnalysisContext context, References refs, HashSet<string> xamlWords, string xamlText,
                HashSet<ISymbol> overrides, List<INamedTypeSymbol> types)
            {
                _context = context;
                _refs = refs;
                _xamlWords = xamlWords;
                _xamlText = xamlText;
                _overrides = overrides;
                _types = types;
            }

            public void Check(INamedTypeSymbol type)
            {
                if (type.TypeKind == TypeKind.Delegate || IsGenerated(type))
                {
                    return;
                }

                CheckRedeclaredState(type);

                foreach (var member in type.GetMembers())
                {
                    if (member.IsImplicitlyDeclared || member is INamedTypeSymbol || IsGenerated(member))
                    {
                        continue;
                    }

                    if (member is IMethodSymbol method && (method.MethodKind != MethodKind.Ordinary || method.PartialImplementationPart != null || method.PartialDefinitionPart != null))
                    {
                        continue;
                    }

                    if (member is IFieldSymbol field && field.Name.EndsWith("Property", StringComparison.Ordinal) && field.Type.Name == "DependencyProperty")
                    {
                        // used by the XAML when its property is
                        continue;
                    }

                    if (member.ContainingType.Name == "PreferenceConstants" && member is IFieldSymbol key && key.IsConst && key.Type.SpecialType == SpecialType.System_String)
                    {
                        _preferenceKeys.Add(key);
                    }

                    if (member is IMethodSymbol withOptional && withOptional.Parameters.Any(p => p.IsOptional))
                    {
                        foreach (var parameter in withOptional.Parameters.Where(p => p.IsOptional && !p.GetAttributes().Any(a => a.AttributeClass?.Name.StartsWith("Caller", StringComparison.Ordinal) == true)))
                        {
                            _optional.Add((withOptional, parameter));
                        }
                    }

                    CollectShadowState(member);
                    CheckTrustedStorage(member);

                    if (member.DeclaredAccessibility == Accessibility.Private)
                    {
                        CheckPrivateField(member);
                        continue;
                    }

                    if (type.TypeKind == TypeKind.Interface)
                    {
                        CheckInterfaceMember(member);
                        continue;
                    }

                    CheckMember(member);
                }

                CheckObservableProperties(type);
                if (type.TypeKind == TypeKind.Enum)
                {
                    CheckEnumMembers(type);
                }
            }

            /// <summary>
            ///     A member that is only ever matched (a switch arm, a comparison) and never produced
            ///     (assigned, passed, returned, a default) cannot reach those arms. A name the XAML uses
            ///     counts as produced.
            /// </summary>
            private void CheckEnumMembers(INamedTypeSymbol type)
            {
                foreach (var member in type.GetMembers().OfType<IFieldSymbol>())
                {
                    if (member.IsImplicitlyDeclared || InXaml(member.Name))
                    {
                        continue;
                    }

                    var uses = _refs.Of(member).Where(r => !r.IsNameOf).ToList();
                    if (uses.Count == 0 || uses.Any(u => !IsMatchOnly(u.Node)))
                    {
                        continue;
                    }

                    var location = member.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax().GetLocation();
                    if (location != null)
                    {
                        _context.ReportDiagnostic(Diagnostic.Create(Descriptors.EnumMemberNeverProduced, location, type.Name, member.Name));
                    }
                }
            }

            private static bool IsMatchOnly(SyntaxNode node)
            {
                var parent = node.Parent;
                while (parent is ParenthesizedExpressionSyntax)
                {
                    parent = parent.Parent;
                }

                switch (parent)
                {
                    case CaseSwitchLabelSyntax:
                    case ConstantPatternSyntax:
                        return true;
                    case BinaryExpressionSyntax binary:
                        return binary.IsKind(SyntaxKind.EqualsExpression) || binary.IsKind(SyntaxKind.NotEqualsExpression) ||
                               binary.IsKind(SyntaxKind.LessThanExpression) || binary.IsKind(SyntaxKind.LessThanOrEqualExpression) ||
                               binary.IsKind(SyntaxKind.GreaterThanExpression) || binary.IsKind(SyntaxKind.GreaterThanOrEqualExpression);
                    default:
                        return false;
                }
            }

            private static bool IsGenerated(ISymbol symbol)
            {
                return symbol.DeclaringSyntaxReferences.Length == 0 ||
                       symbol.DeclaringSyntaxReferences.All(r => References.IsXamlGenerated(r.SyntaxTree) || References.IsToolkitGenerated(r.SyntaxTree));
            }

            private bool InXaml(string name)
            {
                return _xamlWords.Contains(name);
            }

            private static string Kind(ISymbol member)
            {
                switch (member)
                {
                    case IMethodSymbol: return "Method";
                    case IPropertySymbol: return "Property";
                    case IEventSymbol: return "Event";
                    case IFieldSymbol f when f.ContainingType.TypeKind == TypeKind.Enum: return "Enum member";
                    case IFieldSymbol f when f.IsConst: return "Constant";
                    default: return "Field";
                }
            }

            private static IEnumerable<ISymbol> ImplementedInterfaceMembers(ISymbol member)
            {
                foreach (var iface in member.ContainingType.AllInterfaces)
                {
                    foreach (var candidate in iface.GetMembers())
                    {
                        var impl = member.ContainingType.FindImplementationForInterfaceMember(candidate);
                        if (impl != null && SymbolEqualityComparer.Default.Equals(impl.OriginalDefinition, member.OriginalDefinition))
                        {
                            yield return candidate;
                        }
                    }
                }
            }

            /// <summary>References that count as uses of a class member: its own, plus its interface member's and its overrides'.</summary>
            private List<Reference> UsesOf(ISymbol member, out bool implementsInterface)
            {
                var uses = new List<Reference>(_refs.Of(member));
                implementsInterface = false;
                foreach (var interfaceMember in ImplementedInterfaceMembers(member))
                {
                    implementsInterface = true;
                    uses.AddRange(_refs.Of(interfaceMember));
                }

                return uses;
            }

            private void CheckMember(ISymbol member)
            {
                if (member.IsOverride || member.IsAbstract)
                {
                    return;
                }

                var uses = UsesOf(member, out var implementsInterface);
                var real = uses.Where(u => !u.IsNameOf && !u.IsToolkitGenerated).ToList();
                var name = member.Name;
                var location = member.Locations.FirstOrDefault(l => l.IsInSource);
                if (location == null)
                {
                    return;
                }

                var usedByXaml = InXaml(name) || real.Any(u => u.IsXamlGenerated);

                // A [RelayCommand] method is used through its generated command
                if (member is IMethodSymbol method && method.GetAttributes().Any(a => a.AttributeClass?.Name == "RelayCommandAttribute"))
                {
                    var commandName = Regex.Replace(name, "Async$", string.Empty) + "Command";
                    var command = member.ContainingType.GetMembers(commandName).FirstOrDefault();
                    usedByXaml |= InXaml(commandName) || (command != null && _refs.Of(command).Any(u => !u.IsToolkitGenerated));
                    if (!usedByXaml)
                    {
                        Report(Descriptors.MemberNeverReferenced, location, Kind(member), name);
                    }

                    return;
                }

                // An attached property's accessors are what Owner.Name="..." in XAML calls
                var attached = Regex.Match(name, "^(Get|Set)(\\w+)$");
                if (attached.Success && InXaml(attached.Groups[2].Value) && member.ContainingType.GetMembers().Any(m => m.Name == attached.Groups[2].Value + "Property"))
                {
                    return;
                }

                if (real.Count == 0 && !usedByXaml)
                {
                    if (implementsInterface)
                    {
                        return; // the interface member's own rule reports it
                    }

                    if (member.IsVirtual && _overrides.Contains(member.OriginalDefinition))
                    {
                        return; // the hook is used by its overrides
                    }

                    Report(Descriptors.MemberNeverReferenced, location, Kind(member), name);
                    return;
                }

                if (member.IsVirtual && IsEmptyBody(member) && !_overrides.Contains(member.OriginalDefinition))
                {
                    Report(Descriptors.EmptyVirtualHookNeverOverridden, location, name);
                }

                if (!usedByXaml && !implementsInterface && !member.IsVirtual && real.Count > 0 &&
                    real.All(u => u.FromType != null && IsWithin(u.FromType, member.ContainingType)) &&
                    !(member is IFieldSymbol f && f.ContainingType.TypeKind == TypeKind.Enum))
                {
                    Report(Descriptors.MemberOnlyUsedInside, location, Kind(member), name, member.DeclaredAccessibility.ToString().ToLowerInvariant());
                }

                if ((member is IPropertySymbol || member is IFieldSymbol) && !usedByXaml && real.Count > 0 && real.All(u => u.IsWrite && !u.IsRead))
                {
                    Report(Descriptors.MemberWrittenNeverRead, location, Kind(member), name);
                }
            }

            private static bool IsWithin(INamedTypeSymbol from, INamedTypeSymbol owner)
            {
                for (var t = from; t != null; t = t.ContainingType)
                {
                    if (SymbolEqualityComparer.Default.Equals(t.OriginalDefinition, owner.OriginalDefinition))
                    {
                        return true;
                    }
                }

                return false;
            }

            private static bool IsEmptyBody(ISymbol member)
            {
                var syntax = member.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() as MethodDeclarationSyntax;
                return syntax?.Body != null && syntax.Body.Statements.Count == 0;
            }

            private void CheckInterfaceMember(ISymbol member)
            {
                var uses = _refs.Of(member).Where(u => !u.IsNameOf).ToList();
                var location = member.Locations.FirstOrDefault(l => l.IsInSource);
                if (location == null)
                {
                    return;
                }

                if (uses.Count == 0 && !InXaml(member.Name))
                {
                    // A call on the implementing class itself is a use of the implementation, not of the contract
                    Report(Descriptors.MemberNeverReferenced, location, "Interface member", member.ContainingType.Name + "." + member.Name);
                    return;
                }

                var implementers = _types.Where(t => t.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, member.ContainingType.OriginalDefinition))).ToList();
                if (implementers.Count > 0 && !InXaml(member.Name) &&
                    uses.All(u => u.FromType != null && implementers.Any(impl => IsWithin(u.FromType, impl))))
                {
                    Report(Descriptors.InterfaceMemberOnlyImplementationUses, location, member.ContainingType.Name, member.Name);
                }
            }

            private void CheckPrivateField(ISymbol member)
            {
                if (!(member is IFieldSymbol field) || field.IsConst)
                {
                    return;
                }

                var uses = _refs.Of(field).Where(u => !u.IsToolkitGenerated).ToList();
                var reads = uses.Where(u => u.IsRead).ToList();
                var location = field.Locations.FirstOrDefault(l => l.IsInSource);
                if (location == null)
                {
                    return;
                }

                if (reads.Count > 0 && reads.All(u => u.IsNullCheck))
                {
                    Report(Descriptors.FieldOnlyNullChecked, location, field.Name);
                }
            }

            /// <summary>
            ///     A readonly field or get-only auto-property that its initializer or constructor fills from a
            ///     constructor argument, a required service or a new object cannot be null afterwards.
            /// </summary>
            private void CheckTrustedStorage(ISymbol member)
            {
                if (!IsReadonlyStorage(member, out var initializer))
                {
                    return;
                }

                var uses = _refs.Of(member).Where(u => !u.IsToolkitGenerated).ToList();
                // assignments only: Add or Clear on a collection changes its contents, not the reference
                var writes = uses.Where(u => u.IsWrite && (u.AssignedFrom != null || u.IsRead)).ToList();
                var trusted = (initializer == null ? writes.Count > 0 : IsTrustedSource(initializer, null)) &&
                              writes.All(w => IsConstructor(w.FromMethod) && IsTrustedSource(w.AssignedFrom, w.FromMethod));
                if (!trusted)
                {
                    return;
                }

                foreach (var read in uses.Where(u => u.IsRead && (u.IsNullCheck || u.IsConditionalAccess)))
                {
                    Report(Descriptors.NullCheckOnTrustedField, read.Node.GetLocation(), member.Name);
                }
            }

            private static bool IsConstructor(IMethodSymbol method)
            {
                return method != null && (method.MethodKind == MethodKind.Constructor || method.MethodKind == MethodKind.StaticConstructor);
            }

            private static bool IsReadonlyStorage(ISymbol member, out ExpressionSyntax initializer)
            {
                initializer = null;
                var syntax = member.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
                switch (member)
                {
                    case IFieldSymbol field when field.IsReadOnly && !field.IsConst:
                        initializer = (syntax as VariableDeclaratorSyntax)?.Initializer?.Value;
                        return true;
                    case IPropertySymbol property when property.SetMethod == null && syntax is PropertyDeclarationSyntax declaration &&
                                                       declaration.AccessorList != null && declaration.AccessorList.Accessors.All(a => a.Body == null && a.ExpressionBody == null):
                        initializer = declaration.Initializer?.Value;
                        return true;
                    default:
                        return false;
                }
            }

            private static bool IsTrustedSource(ExpressionSyntax value, IMethodSymbol constructor)
            {
                if (value == null)
                {
                    return false;
                }

                if (value is BinaryExpressionSyntax coalesce && coalesce.IsKind(SyntaxKind.CoalesceExpression) && coalesce.Right is ThrowExpressionSyntax)
                {
                    value = coalesce.Left;
                }

                if (value is BaseObjectCreationExpressionSyntax || value is ArrayCreationExpressionSyntax || value is ImplicitArrayCreationExpressionSyntax)
                {
                    return true;
                }

                if (value is IdentifierNameSyntax id)
                {
                    return constructor != null && constructor.Parameters.Any(p => p.Name == id.Identifier.Text);
                }

                var invocation = value as InvocationExpressionSyntax;
                var callee = (invocation?.Expression as MemberAccessExpressionSyntax)?.Name ?? invocation?.Expression as SimpleNameSyntax;
                return callee?.Identifier.Text == "GetRequiredService";
            }

            private void CheckObservableProperties(INamedTypeSymbol type)
            {
                foreach (var field in type.GetMembers().OfType<IFieldSymbol>())
                {
                    if (!field.GetAttributes().Any(a => a.AttributeClass?.Name == "ObservablePropertyAttribute"))
                    {
                        continue;
                    }

                    var propertyName = PropertyNameOf(field.Name);
                    var property = type.GetMembers(propertyName).OfType<IPropertySymbol>().FirstOrDefault();
                    if (property == null)
                    {
                        continue;
                    }

                    var location = field.Locations.FirstOrDefault(l => l.IsInSource);
                    var propertyUses = _refs.Of(property).Where(u => !u.IsToolkitGenerated && !u.IsNameOf).ToList();
                    var fieldUses = _refs.Of(field).Where(u => !u.IsToolkitGenerated).ToList();
                    var bound = InXaml(propertyName) || propertyUses.Any(u => u.IsXamlGenerated && u.IsRead);
                    var read = bound || propertyUses.Any(u => u.IsRead) || fieldUses.Any(u => u.IsRead);
                    var written = propertyUses.Any(u => u.IsWrite) || fieldUses.Any(u => u.IsWrite) ||
                                  Regex.IsMatch(_xamlText, "\\b" + Regex.Escape(propertyName) + "\\b[^}]*Mode=TwoWay");

                    if (!read && (propertyUses.Count > 0 || fieldUses.Count > 0))
                    {
                        Report(Descriptors.MemberWrittenNeverRead, location, "Observable property", propertyName);
                    }
                    else if (!written && !IsCollection(field.Type))
                    {
                        Report(Descriptors.ObservableStateNeverSet, location, propertyName);
                    }
                }
            }

            private static bool IsCollection(ITypeSymbol type)
            {
                return type.Name.Contains("Collection") || type.Name.StartsWith("List", StringComparison.Ordinal) || type.Name.StartsWith("IList", StringComparison.Ordinal);
            }

            private static string PropertyNameOf(string fieldName)
            {
                var name = fieldName.TrimStart('_');
                return name.Length == 0 ? fieldName : char.ToUpperInvariant(name[0]) + name.Substring(1);
            }

            private void CheckRedeclaredState(INamedTypeSymbol type)
            {
                var own = Storage(type).ToList();
                if (own.Count == 0)
                {
                    return;
                }

                for (var baseType = type.BaseType; baseType != null && SymbolEqualityComparer.Default.Equals(baseType.ContainingAssembly, _context.Compilation.Assembly); baseType = baseType.BaseType)
                {
                    var baseNames = new HashSet<string>(Storage(baseType).Select(Named), StringComparer.Ordinal);
                    foreach (var member in own)
                    {
                        if (baseNames.Contains(Named(member)))
                        {
                            Report(Descriptors.StateRedeclaredFromBase, member.Locations.First(l => l.IsInSource), type.Name, Named(member), baseType.Name);
                        }
                    }
                }
            }

            private static string Named(ISymbol member)
            {
                return member is IFieldSymbol f && f.GetAttributes().Any(a => a.AttributeClass?.Name == "ObservablePropertyAttribute")
                    ? PropertyNameOf(f.Name)
                    : member.Name;
            }

            /// <summary>Fields, auto-properties and [ObservableProperty] fields: what holds state, as opposed to computing it.</summary>
            private static IEnumerable<ISymbol> Storage(INamedTypeSymbol type)
            {
                foreach (var member in type.GetMembers())
                {
                    if (member.IsImplicitlyDeclared || member.IsStatic || IsGenerated(member))
                    {
                        continue;
                    }

                    if (member is IFieldSymbol field && !field.IsConst)
                    {
                        yield return field;
                    }
                    else if (member is IPropertySymbol property && !property.IsOverride &&
                             property.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is PropertyDeclarationSyntax syntax &&
                             syntax.AccessorList != null && syntax.AccessorList.Accessors.All(a => a.Body == null && a.ExpressionBody == null) &&
                             !syntax.Modifiers.Any(SyntaxKind.NewKeyword))
                    {
                        yield return property;
                    }
                }
            }

            public void CheckOptionalParameters()
            {
                // Calls reach an implementation through its interface member or base declaration: group them
                var groups = new Dictionary<ISymbol, List<IMethodSymbol>>(SymbolEqualityComparer.Default);
                foreach (var method in _optional.Select(o => o.Method).Distinct(SymbolEqualityComparer.Default).Cast<IMethodSymbol>())
                {
                    var canonical = Canonical(method);
                    if (!groups.TryGetValue(canonical, out var list))
                    {
                        groups[canonical] = list = new List<IMethodSymbol>();
                    }

                    if (!list.Contains(method))
                    {
                        list.Add(method);
                    }
                }

                foreach (var pair in groups)
                {
                    var methods = pair.Value.Concat(new[] { pair.Key as IMethodSymbol }).Where(m => m != null).Distinct(SymbolEqualityComparer.Default).Cast<IMethodSymbol>().ToList();
                    var invocations = new List<InvocationExpressionSyntax>();
                    var methodGroupUse = false;
                    foreach (var method in methods)
                    {
                        foreach (var use in _refs.Of(method).Where(u => !u.IsToolkitGenerated))
                        {
                            if (use.IsInvocation && use.Node.Parent is InvocationExpressionSyntax invocation)
                            {
                                invocations.Add(invocation);
                            }
                            else if (!use.IsNameOf)
                            {
                                methodGroupUse = true;
                            }
                        }
                    }

                    if (methodGroupUse || invocations.Count == 0)
                    {
                        continue;
                    }

                    var reportOn = methods.OrderBy(m => m.ContainingType.TypeKind == TypeKind.Interface ? 0 : 1).First();
                    foreach (var parameter in reportOn.Parameters.Where(p => p.IsOptional && !p.GetAttributes().Any(a => a.AttributeClass?.Name.StartsWith("Caller", StringComparison.Ordinal) == true)))
                    {
                        var passed = 0;
                        foreach (var invocation in invocations)
                        {
                            var model = _context.Compilation.GetSemanticModel(invocation.SyntaxTree);
                            if (model.GetOperation(invocation) is IInvocationOperation operation)
                            {
                                var argument = operation.Arguments.FirstOrDefault(a => a.Parameter?.Ordinal == parameter.Ordinal);
                                if (argument != null && argument.ArgumentKind == ArgumentKind.Explicit)
                                {
                                    passed++;
                                }
                            }
                        }

                        var location = parameter.Locations.FirstOrDefault(l => l.IsInSource);
                        if (location == null)
                        {
                            continue;
                        }

                        if (passed == 0)
                        {
                            Report(Descriptors.OptionalParameterNeverPassed, location, parameter.Name, reportOn.Name);
                        }
                        else if (passed == invocations.Count)
                        {
                            Report(Descriptors.OptionalParameterAlwaysPassed, location, parameter.Name, reportOn.Name);
                        }
                    }
                }
            }

            private static ISymbol Canonical(IMethodSymbol method)
            {
                var interfaceMember = ImplementedInterfaceMembers(method).FirstOrDefault();
                if (interfaceMember != null)
                {
                    return interfaceMember.OriginalDefinition;
                }

                var root = method;
                while (root.OverriddenMethod != null)
                {
                    root = root.OverriddenMethod;
                }

                return root.OriginalDefinition;
            }

            public void CheckPreferenceKeys()
            {
                foreach (var key in _preferenceKeys)
                {
                    var writes = false;
                    var reads = false;
                    foreach (var use in _refs.Of(key))
                    {
                        var call = EnclosingCallName(use.Node);
                        if (call == "SetValue" || call == "SaveAsync" || IsIndexAssignmentTarget(use.Node))
                        {
                            writes = true;
                        }
                        else if (call == "GetValue" || call == "LoadAsync" || call == "TryGetValue" || use.Node.Parent is BinaryExpressionSyntax)
                        {
                            reads = true;
                        }
                    }

                    if (writes && reads)
                    {
                        continue;
                    }

                    Report(Descriptors.PreferenceKeyOneSided, key.Locations.First(l => l.IsInSource), key.Name, writes ? "never read" : reads ? "never written" : "never used");
                }
            }

            private static string EnclosingCallName(SyntaxNode node)
            {
                var argument = node.FirstAncestorOrSelf<ArgumentSyntax>();
                var invocation = argument?.Parent?.Parent as InvocationExpressionSyntax;
                var callee = invocation?.Expression;
                return (callee as MemberAccessExpressionSyntax)?.Name.Identifier.Text ?? (callee as SimpleNameSyntax)?.Identifier.Text;
            }

            private static bool IsIndexAssignmentTarget(SyntaxNode node)
            {
                var argument = node.FirstAncestorOrSelf<ArgumentSyntax>();
                var element = argument?.Parent?.Parent as ElementAccessExpressionSyntax;
                return element?.Parent is AssignmentExpressionSyntax assignment && assignment.Left == element;
            }

            /// <summary>An InvalidOperationException filter on a message text nothing in the code produces any more.</summary>
            public void CheckExceptionMessageFilters()
            {
                foreach (var tree in _refs.SourceTrees)
                {
                    foreach (var filter in tree.GetRoot().DescendantNodes().OfType<CatchFilterClauseSyntax>())
                    {
                        foreach (var invocation in filter.DescendantNodes().OfType<InvocationExpressionSyntax>())
                        {
                            if (!(invocation.Expression is MemberAccessExpressionSyntax access) || access.Name.Identifier.Text != "Contains" ||
                                !(access.Expression is MemberAccessExpressionSyntax message) || message.Name.Identifier.Text != "Message")
                            {
                                continue;
                            }

                            if (!(invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax literal) || !literal.IsKind(SyntaxKind.StringLiteralExpression))
                            {
                                continue;
                            }

                            var text = literal.Token.ValueText;
                            var producers = _refs.StringLiterals.Count(s => s.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0);
                            if (producers <= 1)
                            {
                                Report(Descriptors.ExceptionMessageRuleOrphaned, literal.GetLocation(), text);
                            }
                        }
                    }
                }
            }

            public void CheckRdXml(List<AdditionalText> files)
            {
                foreach (var file in files)
                {
                    var text = file.GetText();
                    if (text == null)
                    {
                        continue;
                    }

                    var content = text.ToString();
                    foreach (Match m in Regex.Matches(content, "<Type\\s+Name=\"(Gelatinarm\\.[\\w.]+)\""))
                    {
                        var fullName = m.Groups[1].Value;
                        var simple = fullName.Substring(fullName.LastIndexOf('.') + 1);
                        if (_types.Any(t => t.Name == simple))
                        {
                            continue;
                        }

                        var span = new TextSpan(m.Groups[1].Index, m.Groups[1].Length);
                        Report(Descriptors.RdXmlTypeMissing, Location.Create(file.Path, span, text.Lines.GetLinePositionSpan(span)), fullName);
                    }
                }
            }

            private void CollectShadowState(ISymbol member)
            {
                if (!(member is IFieldSymbol field) || field.IsReadOnly || field.IsConst || field.DeclaredAccessibility != Accessibility.Private || !field.Name.StartsWith("_", StringComparison.Ordinal))
                {
                    return;
                }

                // A data holder (a type with no methods: preferences, parameters, models) owns no state to shadow
                if (!field.ContainingType.GetMembers().OfType<IMethodSymbol>().Any(m => m.MethodKind == MethodKind.Ordinary))
                {
                    return;
                }

                if (ShadowExcludedTypes.Any(field.Type.Name.Contains) || field.GetAttributes().Any(a => a.AttributeClass?.Name == "ObservablePropertyAttribute"))
                {
                    return;
                }

                // reassigned outside a constructor
                if (!_refs.Of(field).Any(u => u.IsWrite && !u.IsToolkitGenerated && u.FromMethod?.MethodKind != MethodKind.Constructor))
                {
                    return;
                }

                var key = field.Name.TrimStart('_');
                if (!_shadowState.TryGetValue(key, out var list))
                {
                    _shadowState[key] = list = new List<IFieldSymbol>();
                }

                list.Add(field);
            }

            public void CheckShadowState()
            {
                var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var firstTree = _refs.SourceTrees.FirstOrDefault();
                if (firstTree != null && _context.Options.AnalyzerConfigOptionsProvider.GetOptions(firstTree).TryGetValue("gelatinarm_shadow_state_known", out var list))
                {
                    known.UnionWith(list.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0));
                }

                foreach (var pair in _shadowState)
                {
                    var classes = pair.Value.Select(f => f.ContainingType.OriginalDefinition).Distinct(SymbolEqualityComparer.Default).Cast<INamedTypeSymbol>().ToList();
                    if (classes.Count < 2 || known.Contains(pair.Key))
                    {
                        continue;
                    }

                    var names = string.Join(", ", classes.Select(c => c.Name).OrderBy(n => n));
                    foreach (var field in pair.Value)
                    {
                        Report(Descriptors.ShadowState, field.Locations.First(l => l.IsInSource), field.Name, names);
                    }
                }
            }

            private void Report(DiagnosticDescriptor descriptor, Location location, params object[] args)
            {
                _context.ReportDiagnostic(Diagnostic.Create(descriptor, location, args));
            }
        }
    }
}
