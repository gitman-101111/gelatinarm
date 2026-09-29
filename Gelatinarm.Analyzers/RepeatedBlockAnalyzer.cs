using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Gelatinarm.Analyzers
{
    /// <summary>
    ///     A run of statements that appears twice, compared with identifiers and literals blanked
    ///     (the owner's rule: a block of code is written once). Declarative tables are skipped, named
    ///     in gelatinarm_clone_skip_files in .editorconfig.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class RepeatedBlockAnalyzer : DiagnosticAnalyzer
    {
        private const int Window = 6;

        private static readonly HashSet<string> Keywords = new HashSet<string>(
            ("abstract as async await base bool break case catch char class const continue decimal default delegate do double else enum " +
             "event explicit false finally fixed float for foreach get goto if implicit in int interface internal is lock long nameof new null " +
             "object operator out override params partial private protected public readonly ref return sealed set short sizeof static string " +
             "struct switch this throw true try typeof uint ulong unchecked unsafe ushort using var virtual void volatile when where while yield value")
            .Split(' '));

        private static readonly Regex Literal = new Regex(@"""(?:[^""\\]|\\.)*""", RegexOptions.Compiled);
        private static readonly Regex Number = new Regex(@"\b\d+(\.\d+)?\b", RegexOptions.Compiled);
        private static readonly Regex Identifier = new Regex(@"\b[_a-z][A-Za-z0-9_]*\b", RegexOptions.Compiled);
        private static readonly Regex Trivial = new Regex(
            @"^[{}();,]*$|^(else|try|finally|break;|return;|v;|v,|v\)|v\);)$|^(case|default)\b|^v = v;$|\?\? throw new ArgumentNullException|^[A-Z]\w*(<[^>]*>)? v[,)]|^\) : base\(|^public \w+\($",
            RegexOptions.Compiled);
        private static readonly Regex Declaration = new Regex(
            @"^(?:(?:private|protected|public|static|readonly) )+[^()=]*(?: = [^;]*)?;$|^(?:(?:private|protected|public|static|override|virtual) )+[\w<>\[\],.? ]+ [A-Z]\w*$|^(?:private )?(?:get|set)(?: => [^;]*;)?$|^if \(SetProperty\(ref v, value\)\)$|^(?:(?:private|protected|public) )+[\w<>\[\],.? ]+ [A-Z]\w* \{ get",
            RegexOptions.Compiled);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Descriptors.RepeatedBlock);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationAction(Analyze);
        }

        private static void Analyze(CompilationAnalysisContext context)
        {
            var index = new Dictionary<string, List<(SyntaxTree Tree, int Line, int Start, int End)>>(StringComparer.Ordinal);
            foreach (var tree in context.Compilation.SyntaxTrees)
            {
                if (References.IsXamlGenerated(tree) || References.IsToolkitGenerated(tree) || IsSkipped(context, tree))
                {
                    continue;
                }

                var lines = Normalize(tree);
                for (var k = 0; k + Window <= lines.Count; k++)
                {
                    var window = lines.Skip(k).Take(Window).ToList();
                    if (window.All(l => Declaration.IsMatch(l.Text) || IsTableRow(l.Text)))
                    {
                        continue;
                    }

                    var key = string.Join("\n", window.Select(l => l.Text));
                    if (!index.TryGetValue(key, out var list))
                    {
                        index[key] = list = new List<(SyntaxTree, int, int, int)>();
                    }

                    list.Add((tree, window[0].Line, window[0].Start, window[Window - 1].End));
                }
            }

            var reported = new HashSet<(SyntaxTree, int)>();
            foreach (var pair in index.Where(p => p.Value.Count > 1))
            {
                // one report per run: the first window of a longer clone speaks for the whole run
                var sites = pair.Value.OrderBy(s => s.Tree.FilePath, StringComparer.Ordinal).ThenBy(s => s.Line).ToList();
                foreach (var site in sites)
                {
                    if (Enumerable.Range(site.Line - Window, Window).Any(l => reported.Contains((site.Tree, l))))
                    {
                        reported.Add((site.Tree, site.Line));
                        continue;
                    }

                    reported.Add((site.Tree, site.Line));
                    var others = string.Join(", ", sites.Where(s => s != site).Select(s => $"{Path.GetFileName(s.Tree.FilePath)}:{s.Line}"));
                    var span = TextSpan.FromBounds(site.Start, site.End);
                    context.ReportDiagnostic(Diagnostic.Create(Descriptors.RepeatedBlock, Location.Create(site.Tree, span), Window, others));
                }
            }
        }

        private static bool IsSkipped(CompilationAnalysisContext context, SyntaxTree tree)
        {
            if (!context.Options.AnalyzerConfigOptionsProvider.GetOptions(tree).TryGetValue("gelatinarm_clone_skip_files", out var list))
            {
                return false;
            }

            var path = tree.FilePath.Replace('\\', '/');
            return list.Split(',').Select(s => s.Trim()).Any(s => s.Length > 0 && path.EndsWith(s, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsTableRow(string line)
        {
            return (line.Contains("\"S\"") || line.Contains("N")) && Regex.Replace(line, @"""S""|\bN\b|\bv\b|=>|[?:,_().\s]", string.Empty).Length == 0;
        }

        /// <summary>The file's lines, each normalized to its shape: literals to S and N, lower-case identifiers to v.</summary>
        private static List<(string Text, int Line, int Start, int End)> Normalize(SyntaxTree tree)
        {
            var text = tree.GetText();
            var result = new List<(string, int, int, int)>();
            foreach (var line in text.Lines)
            {
                var raw = line.ToString().Trim();
                if (raw.Length == 0 || raw.StartsWith("//") || raw.StartsWith("using ") || raw.StartsWith("namespace ") || raw.StartsWith("[") ||
                    raw.StartsWith("Logger") || raw.StartsWith("_logger") || raw.StartsWith("private readonly ") || raw.StartsWith("public class ") ||
                    raw.StartsWith("public sealed ") || raw.StartsWith("internal ") || raw.StartsWith("public abstract "))
                {
                    continue;
                }

                var normalized = Literal.Replace(raw, "\"S\"");
                normalized = Number.Replace(normalized, "N");
                normalized = Identifier.Replace(normalized, m => Keywords.Contains(m.Value) ? m.Value : "v");
                normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
                if (Trivial.IsMatch(normalized))
                {
                    continue;
                }

                result.Add((normalized, line.LineNumber + 1, line.Start, line.End));
            }

            return result;
        }
    }
}
