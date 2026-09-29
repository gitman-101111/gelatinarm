using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Gelatinarm.Analyzers.Xaml
{
    internal sealed class XamlAttribute
    {
        public string Name;       // as written: x:Name, Grid.Row, ScrollViewer.HorizontalScrollMode
        public string Value;
        public int Line;
        public int Column;
    }

    internal sealed class XamlElement
    {
        public string Name;       // as written: Grid, controls:LoadingOverlay
        public string LocalName;  // Grid, LoadingOverlay
        public XamlElement Parent;
        public readonly List<XamlAttribute> Attributes = new List<XamlAttribute>();
        public readonly List<XamlElement> Children = new List<XamlElement>();        // element children
        public readonly List<XamlElement> PropertyElements = new List<XamlElement>(); // Grid.RowDefinitions and the like
        public int Line;
        public int Column;
        public bool InTemplate;   // inside a ControlTemplate, DataTemplate, Style or VisualStateManager
        public string Text;       // a property element's plain content (<Grid.RowSpacing>2</Grid.RowSpacing>), else null

        public XamlAttribute Attribute(string name)
        {
            return Attributes.FirstOrDefault(a => a.Name == name);
        }

        public string Value(string name)
        {
            return Attribute(name)?.Value;
        }
    }

    /// <summary>
    ///     One XAML file, parsed once per compilation: its elements with line positions, the words
    ///     its attribute values contain (what the code-side rules count as a XAML reference), its
    ///     resource keys and the resources it asks for.
    /// </summary>
    internal sealed class XamlFile
    {
        private static readonly Regex Word = new Regex(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);
        private static readonly Regex StaticResourceRef = new Regex(
            @"StaticResource\s+(?:ResourceKey=)?([\w.]+)\s*\}|<StaticResource\s+ResourceKey=""([\w.]+)""", RegexOptions.Compiled);
        private static readonly Regex MergedSource = new Regex(@"ms-appx:///([\w/]+\.xaml)", RegexOptions.Compiled);

        public string Path { get; private set; }
        public string FileName { get; private set; }
        public SourceText Text { get; private set; }
        public string RawText { get; private set; }
        public string ClassName { get; private set; }          // the x:Class's simple name, or null
        public XamlElement Root { get; private set; }
        public List<XamlElement> Elements { get; } = new List<XamlElement>();
        public HashSet<string> Words { get; } = new HashSet<string>(StringComparer.Ordinal);
        public List<XamlAttribute> Keys { get; } = new List<XamlAttribute>();
        public List<(string Key, int Line, int Column)> StaticResourceReferences { get; } = new List<(string, int, int)>();
        public List<string> MergedSources { get; } = new List<string>();  // the ms-appx paths this file merges, as Styles/Text.xaml
        public bool UsesXBind { get; private set; }
        public bool IsApp => FileName.Equals("App.xaml", StringComparison.OrdinalIgnoreCase);
        public bool IsDictionary => Root.LocalName == "ResourceDictionary";

        /// <summary>App.xaml, or a dictionary it merges: its keys are visible everywhere.</summary>
        public bool IsAppScope { get; set; }

        public bool Merges(XamlFile other)
        {
            var path = other.Path.Replace('\\', '/');
            return MergedSources.Any(s => path.EndsWith("/" + s, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>One lookup over several files' elements: App.xaml and the dictionaries it merges.</summary>
        public static XamlFile Union(XamlFile first, IEnumerable<XamlFile> parts)
        {
            var result = new XamlFile { Path = first.Path, FileName = first.FileName, Text = first.Text, RawText = first.RawText, Root = first.Root };
            result.Elements.AddRange(parts.SelectMany(p => p.Elements));
            return result;
        }

        public static XamlFile TryLoad(AdditionalText file)
        {
            var text = file.GetText();
            if (text == null)
            {
                return null;
            }

            // System.IO.Path spelled out: the Path property above hides it
            var result = new XamlFile { Path = file.Path, FileName = System.IO.Path.GetFileName(file.Path), Text = text, RawText = text.ToString() };
            XDocument doc;
            try
            {
                doc = XDocument.Load(new StringReader(result.RawText), LoadOptions.SetLineInfo);
            }
            catch (XmlException)
            {
                return null; // the XAML compiler reports the syntax error
            }

            if (doc.Root == null)
            {
                return null;
            }

            result.Root = result.Build(doc.Root, null, false);
            result.ClassName = result.Root.Value("x:Class")?.Split('.').Last();
            foreach (Match m in MergedSource.Matches(result.RawText))
            {
                result.MergedSources.Add(m.Groups[1].Value);
            }

            result.UsesXBind = result.RawText.Contains("{x:Bind");
            foreach (Match m in Word.Matches(result.RawText))
            {
                result.Words.Add(m.Value);
            }

            foreach (Match m in StaticResourceRef.Matches(result.RawText))
            {
                var key = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                var pos = result.Text.Lines.GetLinePosition(m.Index);
                result.StaticResourceReferences.Add((key, pos.Line + 1, pos.Character + 1));
            }

            return result;
        }

        private XamlElement Build(XElement x, XamlElement parent, bool inTemplate)
        {
            var name = Prefixed(x, x.Name);
            var element = new XamlElement
            {
                Name = name,
                LocalName = x.Name.LocalName,
                Parent = parent,
                InTemplate = inTemplate
            };
            var info = (IXmlLineInfo)x;
            element.Line = info.LineNumber;
            element.Column = info.LinePosition;
            Elements.Add(element);

            foreach (var a in x.Attributes())
            {
                var attrInfo = (IXmlLineInfo)a;
                var attribute = new XamlAttribute
                {
                    Name = a.IsNamespaceDeclaration ? (a.Name.LocalName == "xmlns" ? "xmlns" : "xmlns:" + a.Name.LocalName) : Prefixed(x, a.Name),
                    Value = a.Value,
                    Line = attrInfo.LineNumber,
                    Column = attrInfo.LinePosition
                };
                element.Attributes.Add(attribute);
                if (attribute.Name == "x:Key")
                {
                    Keys.Add(attribute);
                }
            }

            if (!x.HasElements)
            {
                var text = x.Value.Trim();
                element.Text = text.Length > 0 ? text : null;
            }

            var childrenInTemplate = inTemplate || x.Name.LocalName == "ControlTemplate" || x.Name.LocalName == "DataTemplate" ||
                                     x.Name.LocalName == "ItemsPanelTemplate" || x.Name.LocalName == "Style" ||
                                     x.Name.LocalName == "VisualStateManager.VisualStateGroups";
            foreach (var child in x.Elements())
            {
                var built = Build(child, element, childrenInTemplate);
                // Grid.RowDefinitions, Button.Flyout, Border.Background: a property of the element, not content
                if (child.Name.LocalName.Contains("."))
                {
                    element.PropertyElements.Add(built);
                }
                else
                {
                    element.Children.Add(built);
                }
            }

            return element;
        }

        private static string Prefixed(XElement scope, XName name)
        {
            var prefix = scope.GetPrefixOfNamespace(name.Namespace);
            return string.IsNullOrEmpty(prefix) ? name.LocalName : prefix + ":" + name.LocalName;
        }

        public Location LocationOf(XamlElement element)
        {
            return LocationAt(element.Line, element.Column, element.Name.Length + 1);
        }

        public Location LocationOf(XamlAttribute attribute)
        {
            return LocationAt(attribute.Line, attribute.Column, attribute.Name.Length + attribute.Value.Length + 3);
        }

        public Location LocationAt(int line, int column, int length)
        {
            var lines = Text.Lines;
            var lineIndex = Math.Max(0, Math.Min(line - 1, lines.Count - 1));
            var start = Math.Min(lines[lineIndex].Start + Math.Max(0, column - 1), Text.Length);
            var span = new TextSpan(start, Math.Max(0, Math.Min(length, Text.Length - start)));
            return Location.Create(Path, span, lines.GetLinePositionSpan(span));
        }

        /// <summary>
        ///     Whether the word appears in the file outside the one attribute that declares it.
        /// </summary>
        public bool MentionsElsewhere(string word)
        {
            return Regex.Matches(RawText, @"\b" + Regex.Escape(word) + @"\b").Count > 1;
        }
    }
}
