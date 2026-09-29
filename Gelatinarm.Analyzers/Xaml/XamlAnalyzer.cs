using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Gelatinarm.Analyzers.Xaml
{
    /// <summary>
    ///     The XAML rules. The files come in as AdditionalFiles; the code they are checked against is
    ///     the compilation itself (a page's partial class and its bases, string literals anywhere).
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class XamlAnalyzer : DiagnosticAnalyzer
    {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(
            Descriptors.ResourceKeyNeverReferenced, Descriptors.StaticResourceUndefined,
            Descriptors.NamedElementNeverAddressed, Descriptors.LoadingOverlayUnbound,
            Descriptors.XmlnsPrefixUnused, Descriptors.AttributeAtDefault, Descriptors.AttachedFormOnOwner,
            Descriptors.WrapperElement, Descriptors.GridSlotUnused, Descriptors.BindingAmongXBind, Descriptors.ThicknessWrittenLong,
            Descriptors.RepeatedAttributeSet, Descriptors.PropertyElementAsText, Descriptors.MarkupPrefixUndeclared,
            Descriptors.StyleSpelledOut, Descriptors.DesignTimeAttribute, Descriptors.StyleDuplicated,
            Descriptors.ButtonContentCentred, Descriptors.StyleWideAttribute, Descriptors.StyleOverridden, Descriptors.LoneTabIndex,
            Descriptors.AttachedOutsidePanel, Descriptors.PropertyElementAsResource, Descriptors.StretchWithFixedSize,
            Descriptors.AttributeRepeatsStyle, Descriptors.BindModeConvention, Descriptors.PageBackground, Descriptors.TextDimmedByHand,
            Descriptors.ColourLiteral, Descriptors.ProgressBarByHand, Descriptors.GlyphButtonContent, Descriptors.ColourAsBrush,
            Descriptors.LoadingOverlaySpelledOut, Descriptors.IconLabelButton, Descriptors.FillDimmedByHand, Descriptors.MarginInsideSpacing,
            Descriptors.SizeAgainstBounds, Descriptors.SpacingByMargin, Descriptors.SharedStyleOverride);

        // Values an element already has, from the property default or its default style in the SDK's
        // generic.xaml; the same for a Setter in a Style whose TargetType is the element. An element
        // with a Style of its own is skipped for Margin and Padding, a Style with BasedOn altogether.
        private static readonly (string Attribute, string Value, string[] Elements)[] Defaults =
        {
            ("IsTabStop", "True", new[] { "Button", "ToggleButton", "CheckBox", "ComboBox", "TextBox", "ToggleSwitch", "ListViewItem", "GridViewItem" }),
            ("IsTabStop", "False", new[] { "ListView", "GridView", "ScrollViewer", "MediaPlayerElement" }),
            ("HorizontalAlignment", "Stretch", new[] { "Grid", "Border", "StackPanel", "Image", "Rectangle", "TextBlock", "ScrollViewer", "MediaPlayerElement", "Slider", "TextBox" }),
            ("VerticalAlignment", "Stretch", new[] { "Grid", "Border", "StackPanel", "Image", "Rectangle", "ScrollViewer", "MediaPlayerElement" }),
            ("Margin", "0", new[] { "Grid", "StackPanel", "Border", "MediaPlayerElement", "ListView", "GridView", "ListViewItem", "UnwatchedIndicator", "LoadingOverlay", "ScrollViewer" }),
            ("Width", "Auto", new[] { "*" }),
            ("Height", "Auto", new[] { "*" }),
            ("HorizontalContentAlignment", "Center", new[] { "Button", "ToggleButton" }),
            ("VerticalContentAlignment", "Center", new[] { "Button", "ToggleButton" }),
            ("HorizontalAlignment", "Left", new[] { "Button", "ToggleButton", "HyperlinkButton", "CheckBox", "ComboBox", "ToggleSwitch" }),
            ("VerticalAlignment", "Center", new[] { "Button", "ToggleButton", "HyperlinkButton", "CheckBox", "ToggleSwitch" }),
            ("VerticalAlignment", "Top", new[] { "ComboBox" }),
            ("UseSystemFocusVisuals", "True", new[] { "Button", "ToggleButton", "HyperlinkButton", "RepeatButton", "ListViewItem", "GridViewItem", "ListView", "GridView", "ListBox", "ScrollViewer", "CheckBox", "ToggleSwitch", "Slider" }),
            ("TextWrapping", "NoWrap", new[] { "TextBlock" }),
            ("TextAlignment", "Left", new[] { "TextBlock" }),
            ("NavigationCacheMode", "Disabled", new[] { "*" }),
            ("AreTransportControlsEnabled", "False", new[] { "MediaPlayerElement" }),
            ("IsLoading", "False", new[] { "LoadingOverlay" }),
            ("Foreground", "{StaticResource SystemControlForegroundBaseHighBrush}", new[] { "Button", "ToggleButton", "HyperlinkButton", "RepeatButton", "ListViewItem", "GridViewItem" }),
            // Text and icons draw in the theme's text brush (BaseHigh on the dark theme App.xaml requests) or in the
            // brush of the control they sit in, which is BaseHigh in every state but disabled
            ("Foreground", "{StaticResource SystemControlForegroundBaseHighBrush}", new[] { "TextBlock", "FontIcon", "SymbolIcon" }),
            ("Grid.Row", "0", new[] { "*" }),
            ("Grid.Column", "0", new[] { "*" }),
            ("Grid.RowSpan", "1", new[] { "*" }),
            ("Grid.ColumnSpan", "1", new[] { "*" }),
            ("Padding", "0", new[] { "Grid", "Border", "StackPanel", "ListView", "ScrollViewer", "GridViewItem" }),
            ("Stretch", "Uniform", new[] { "Image", "MediaPlayerElement" }),
            ("FontFamily", "Segoe MDL2 Assets", new[] { "FontIcon" }),
            ("BorderThickness", "0", new[] { "BasePage", "DetailsPage", "MusicDetailsPage", "ScrollViewer", "Border" }),
            ("BorderBrush", "Transparent", new[] { "BasePage", "DetailsPage", "MusicDetailsPage", "ScrollViewer" }),
            ("AllowFocusOnInteraction", "True", new[] { "*" }),
            ("IsHitTestVisible", "True", new[] { "*" }),
            ("XYFocusKeyboardNavigation", "Enabled", new[] { "*" }),
            ("HorizontalScrollBarVisibility", "Disabled", new[] { "ScrollViewer" }),
            ("VerticalScrollBarVisibility", "Visible", new[] { "ScrollViewer" }),
            ("IsVerticalRailEnabled", "True", new[] { "ScrollViewer" }),
            ("IsHorizontalRailEnabled", "True", new[] { "ScrollViewer" }),
            ("HorizontalScrollMode", "Auto", new[] { "ScrollViewer" }),
            ("VerticalScrollMode", "Auto", new[] { "ScrollViewer" }),
            ("ZoomMode", "Disabled", new[] { "ScrollViewer" }),
            ("TabNavigation", "Once", new[] { "ListView", "GridView" }),
            ("IsItemClickEnabled", "False", new[] { "ListView", "GridView" }),
            ("IsSwipeEnabled", "True", new[] { "ListView", "GridView" }),
            ("ScrollViewer.BringIntoViewOnFocusChange", "True", new[] { "ListView", "GridView" }),
            ("ScrollViewer.IsVerticalRailEnabled", "True", new[] { "ListView", "GridView" }),
            ("ScrollViewer.IsHorizontalRailEnabled", "False", new[] { "ListView", "GridView" }),
            ("ScrollViewer.VerticalScrollMode", "Enabled", new[] { "ListView", "GridView" }),
            ("ScrollViewer.HorizontalScrollMode", "Disabled", new[] { "ListView", "GridView" }),
            ("ScrollViewer.HorizontalScrollBarVisibility", "Disabled", new[] { "ListView", "GridView" }),
            ("ScrollViewer.VerticalScrollBarVisibility", "Auto", new[] { "ListView", "GridView" }),
            ("ScrollViewer.ZoomMode", "Disabled", new[] { "ListView", "GridView" }),
            ("ScrollViewer.IsDeferredScrollingEnabled", "False", new[] { "ListView", "GridView" }),
        };

        // Theme resources the platform's default templates define; not ours to declare
        private static readonly Regex PlatformKey = new Regex(
            "^(System|Text|Body|Caption|Subtitle|Title|Header|Base|ContentControl|Button|ListView|GridView|ComboBox|ToggleSwitch|Slider|Flyout|ScrollViewer|Pivot|TextControl|Toggle|Control|Accent|Application|ListViewItem|AppBar)",
            RegexOptions.Compiled);

        private static readonly Regex StaticResourceValue = new Regex(@"^\{StaticResource\s+(\w+)\}$", RegexOptions.Compiled);

        // Attributes a Style cannot hold: an event handler is wired per element
        private static readonly Regex EventAttribute = new Regex("(Click|Changed|Loaded|Unloaded|KeyDown|KeyUp|Opening|Opened|Closing|Closed|Focus|Tapped|Requested)$", RegexOptions.Compiled);

        private static readonly HashSet<string> PositioningAttributes = new HashSet<string>
        {
            "Grid.Row", "Grid.Column", "Grid.RowSpan", "Grid.ColumnSpan", "Canvas.ZIndex"
        };

        private static readonly HashSet<string> WrapperCandidates = new HashSet<string> { "Grid", "Border", "StackPanel", "Canvas", "Viewbox" };

        // Size, margin, alignment and visibility mean the same on a Grid or Border as on its one
        // child, which fills it, unless the child sets the same attribute to another value; a
        // StackPanel does not size its child, a Viewbox scales it, a Canvas leaves it at its
        // desired size, so on those they stay
        private static readonly HashSet<string> PassThroughAttributes = new HashSet<string>
        {
            "Width", "Height", "MinWidth", "MinHeight", "MaxWidth", "MaxHeight", "Margin", "HorizontalAlignment", "VerticalAlignment", "Visibility"
        };

        private static readonly HashSet<string> PassThroughWrappers = new HashSet<string> { "Grid", "Border" };

        private static readonly HashSet<string> ThicknessAttributes = new HashSet<string> { "Margin", "Padding", "BorderThickness", "FocusVisualMargin" };

        // What a Style would carry: looks, not placement (Margin, alignment and Grid.* say where one element goes)
        private static readonly HashSet<string> StylingAttributes = new HashSet<string>
        {
            "Width", "Height", "MinWidth", "MinHeight", "MaxWidth", "MaxHeight", "Padding", "Margin", "Background", "BorderThickness", "BorderBrush",
            "Foreground", "FontSize", "FontWeight", "FontFamily", "Opacity", "UseSystemFocusVisuals", "CornerRadius", "TextWrapping", "TextTrimming", "Style",
            "HorizontalScrollMode", "VerticalScrollMode", "HorizontalScrollBarVisibility", "VerticalScrollBarVisibility",
            "ScrollViewer.HorizontalScrollMode", "ScrollViewer.VerticalScrollMode", "ScrollViewer.HorizontalScrollBarVisibility",
            "ScrollViewer.VerticalScrollBarVisibility", "ScrollViewer.IsHorizontalRailEnabled", "ScrollViewer.IsVerticalRailEnabled"
        };

        private static bool IsBound(string value)
        {
            return value.StartsWith("{x:Bind", StringComparison.Ordinal) || value.StartsWith("{Binding", StringComparison.Ordinal) ||
                   value.StartsWith("{TemplateBinding", StringComparison.Ordinal);
        }

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationAction(compilation =>
            {
                var files = compilation.Options.AdditionalFiles
                    .Where(f => f.Path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                    .Select(XamlFile.TryLoad)
                    .Where(f => f != null)
                    .ToList();
                if (files.Count > 0)
                {
                    Report(compilation, files);
                }
            });
        }

        private static void Report(CompilationAnalysisContext context, List<XamlFile> files)
        {
            var code = new CodeIndex(context.Compilation);
            var definedKeys = new HashSet<string>(files.SelectMany(f => f.Keys.Select(k => k.Value)));
            var appFile = files.FirstOrDefault(f => f.IsApp);
            foreach (var file in files)
            {
                file.IsAppScope = file.IsApp || (appFile != null && appFile.Merges(file));
            }

            var appScope = files.Where(f => f.IsAppScope).ToList();
            var appStyles = appScope.SelectMany(KeyedStyles).ToList();
            var app = appFile == null ? null : XamlFile.Union(appFile, appScope);
            ReportStyleWideAttributes(context, files, app);
            ReportSharedStyleOverrides(context, files);

            foreach (var file in files)
            {
                ReportResourceKeys(context, file, files, code);
                foreach (var (key, line, column) in file.StaticResourceReferences)
                {
                    if (!definedKeys.Contains(key) && !PlatformKey.IsMatch(key))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(Descriptors.StaticResourceUndefined, file.LocationAt(line, column, key.Length + 16), key));
                    }
                }

                ReportXmlns(context, file);
                ReportDesignTime(context, file);
                ReportUndeclaredPrefixes(context, file);
                var stylesInScope = appStyles.Concat(file.IsAppScope ? Enumerable.Empty<(string, string, List<(string, string)>)>() : KeyedStyles(file)).ToList();
                ReportSpelledOutStyles(context, file, stylesInScope);
                ReportOverriddenStyles(context, file, stylesInScope);
                ReportLoneTabIndex(context, file);
                ReportBindMode(context, file);
                ReportPageBackground(context, file);
                ReportRepeatedAttributeSets(context, file);
                ReportIdenticalStyles(context, file, KeyedStyles(file).ToList());
                var pageIdentifiers = code.IdentifiersOfClassAndBases(file.ClassName);
                foreach (var element in file.Elements)
                {
                    ReportNamedElement(context, file, element, pageIdentifiers, code);
                    ReportLoadingOverlay(context, file, element, pageIdentifiers);
                    ReportDefaults(context, file, app, element);
                    ReportButtonContentCentring(context, file, app, element);
                    ReportAttachedOutsidePanel(context, file, element);
                    ReportStretchWithFixedSize(context, file, element);
                    ReportAttributeRepeatsStyle(context, file, app, element);
                    ReportTextDimmedByHand(context, file, element);
                    ReportColourLiteral(context, file, element);
                    ReportProgressBar(context, file, element);
                    ReportGlyphButton(context, file, element);
                    ReportLoadingOverlaySpelledOut(context, file, element);
                    ReportIconLabelButton(context, file, element);
                    ReportFillDimmedByHand(context, file, element);
                    ReportMarginInsideSpacing(context, file, app, element);
                    ReportSizeAgainstBounds(context, file, app, element);
                    ReportSpacingByMargin(context, file, element);
                    ReportColourAsBrush(context, file, element);
                    ReportWrapper(context, file, element);
                    ReportGridSlots(context, file, element);
                    ReportBinding(context, file, element);
                    ReportThickness(context, file, element);
                    ReportPropertyElementText(context, file, element);
                }
            }
        }

        private static void ReportResourceKeys(CompilationAnalysisContext context, XamlFile file, List<XamlFile> all, CodeIndex code)
        {
            foreach (var key in file.Keys)
            {
                // A platform theme key redefined in a page's resources (lightweight styling) is read by
                // the default template implicitly, never through {StaticResource}
                if (PlatformKey.IsMatch(key.Value))
                {
                    continue;
                }

                IEnumerable<XamlFile> scope;
                IEnumerable<string> classes;
                if (file.IsAppScope)
                {
                    scope = all;
                    classes = null; // any code
                }
                else if (file.IsDictionary)
                {
                    var mergers = all.Where(f => f.Merges(file)).ToList();
                    scope = new[] { file }.Concat(mergers);
                    classes = mergers.Select(m => m.ClassName);
                }
                else
                {
                    scope = new[] { file };
                    classes = new[] { file.ClassName };
                }

                var name = key.Value;
                var referenced = scope.Any(f => Regex.IsMatch(f.RawText,
                    @"(?:StaticResource|ThemeResource|CustomResource|ResourceKey="")\s*" + Regex.Escape(name) + @"\b"));
                if (!referenced)
                {
                    referenced = code.StringLiteralExists(name, classes);
                }

                if (!referenced)
                {
                    context.ReportDiagnostic(Diagnostic.Create(Descriptors.ResourceKeyNeverReferenced, file.LocationOf(key), name));
                }
            }
        }

        /// <summary>A file's keyed Styles that are plain setter lists: (key, TargetType, setters); BasedOn and Template styles are left out.</summary>
        private static IEnumerable<(string Key, string TargetType, List<(string Property, string Value)> Setters)> KeyedStyles(XamlFile file)
        {
            foreach (var style in file.Elements.Where(e => e.LocalName == "Style" && e.Attribute("x:Key") != null && e.Attribute("BasedOn") == null))
            {
                var target = style.Value("TargetType");
                if (target == null || style.Children.Any(c => c.LocalName != "Setter" || c.Value("Property") == null || c.Value("Value") == null))
                {
                    continue;
                }

                var setters = style.Children.Select(c => (c.Value("Property"), c.Value("Value"))).ToList();
                if (setters.Count >= 2)
                {
                    yield return (style.Value("x:Key"), target, setters);
                }
            }
        }

        /// <summary>Two keyed Styles of one TargetType with the same setters: one key would do.</summary>
        private static void ReportIdenticalStyles(CompilationAnalysisContext context, XamlFile file,
            List<(string Key, string TargetType, List<(string Property, string Value)> Setters)> styles)
        {
            for (var i = 0; i < styles.Count; i++)
            {
                for (var j = i + 1; j < styles.Count; j++)
                {
                    if (styles[i].TargetType != styles[j].TargetType || styles[i].Setters.Count != styles[j].Setters.Count ||
                        !styles[i].Setters.All(s => styles[j].Setters.Any(t => t.Property == s.Property && ValueEquals(t.Value, s.Value))))
                    {
                        continue;
                    }

                    var duplicate = file.Elements.First(e => e.LocalName == "Style" && e.Value("x:Key") == styles[j].Key);
                    context.ReportDiagnostic(Diagnostic.Create(Descriptors.StyleDuplicated, file.LocationOf(duplicate), styles[j].Key, styles[i].Key));
                }
            }
        }

        /// <summary>
        ///     A Button's content panel centring itself: a Button centres its content by default, so only a
        ///     Style (found in the page or App.xaml) that moves the content elsewhere makes the panel's own
        ///     alignment mean something. A Style that cannot be found is taken to move it.
        /// </summary>
        private static void ReportButtonContentCentring(CompilationAnalysisContext context, XamlFile file, XamlFile app, XamlElement element)
        {
            var button = element.Parent;
            if (button?.LocalName != "Button")
            {
                return;
            }

            var styleValue = button.Value("Style");
            var styleKey = StaticResourceValue.Match(styleValue ?? string.Empty);
            var style = styleKey.Success ? KeyedElement(file, app, "Style", styleKey.Groups[1].Value) : null;
            if (styleValue != null && style == null)
            {
                return;
            }

            foreach (var axis in new[] { "Horizontal", "Vertical" })
            {
                var attribute = element.Attribute(axis + "Alignment");
                var contentSetter = style?.Children.FirstOrDefault(c => c.LocalName == "Setter" && c.Value("Property") == axis + "ContentAlignment");
                if (attribute != null && ValueEquals(attribute.Value, "Center") &&
                    (contentSetter == null || ValueEquals(contentSetter.Value("Value") ?? string.Empty, "Center")))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Descriptors.ButtonContentCentred, file.LocationOf(attribute), attribute.Name));
                }
            }
        }

        /// <summary>
        ///     One Style and one styling attribute at one value, together on six or more elements across three
        ///     or more files: a derived Style in App.xaml.
        /// </summary>
        private static void ReportSharedStyleOverrides(CompilationAnalysisContext context, List<XamlFile> files)
        {
            var groups = new Dictionary<string, List<(XamlFile File, XamlElement Element)>>(StringComparer.Ordinal);
            foreach (var file in files)
            {
                foreach (var element in file.Elements)
                {
                    var style = StaticResourceValue.Match(element.Value("Style") ?? string.Empty);
                    if (!style.Success || element.LocalName == "Setter")
                    {
                        continue;
                    }

                    // Wrapping and trimming are how one text lays out, not a look the Style should carry
                    foreach (var attribute in element.Attributes.Where(a => StylingAttributes.Contains(a.Name) && a.Name != "Style" && !IsBound(a.Value) &&
                                                                          !PositioningAttributes.Contains(a.Name) && a.Name != "TextWrapping" && a.Name != "TextTrimming"))
                    {
                        var key = element.LocalName + "|" + style.Groups[1].Value + "|" + attribute.Name + "=" + attribute.Value;
                        if (!groups.TryGetValue(key, out var list))
                        {
                            groups[key] = list = new List<(XamlFile, XamlElement)>();
                        }

                        list.Add((file, element));
                    }
                }
            }

            foreach (var pair in groups.Where(g => g.Value.Count >= 6 && g.Value.Select(u => u.File).Distinct().Count() >= 3))
            {
                var parts = pair.Key.Split('|');
                var attribute = parts[2].Substring(0, parts[2].IndexOf('='));
                var value = parts[2].Substring(parts[2].IndexOf('=') + 1);
                var (firstFile, first) = pair.Value.OrderBy(u => u.File.FileName, StringComparer.Ordinal).ThenBy(u => u.Element.Line).First();
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.SharedStyleOverride, firstFile.LocationOf(first.Attribute(attribute)),
                    pair.Value.Count, pair.Value.Select(u => u.File).Distinct().Count(), parts[1], attribute, value));
            }
        }

        /// <summary>An attribute every element naming one keyed Style sets to the same value: the Style holds it once.</summary>
        private static void ReportStyleWideAttributes(CompilationAnalysisContext context, List<XamlFile> files, XamlFile app)
        {
            var users = new Dictionary<string, List<(XamlFile File, XamlElement Element)>>(StringComparer.Ordinal);
            foreach (var file in files)
            {
                foreach (var element in file.Elements)
                {
                    var m = StaticResourceValue.Match(element.Value("Style") ?? string.Empty);
                    if (!m.Success || element.LocalName == "Setter")
                    {
                        continue;
                    }

                    if (!users.TryGetValue(m.Groups[1].Value, out var list))
                    {
                        users[m.Groups[1].Value] = list = new List<(XamlFile, XamlElement)>();
                    }

                    list.Add((file, element));
                }
            }

            foreach (var pair in users.Where(p => p.Value.Count >= 3))
            {
                var (firstFile, first) = pair.Value[0];
                if (KeyedElement(firstFile, app, "Style", pair.Key) == null)
                {
                    continue;
                }

                foreach (var attribute in first.Attributes)
                {
                    if (attribute.Name == "Style" || attribute.Name.StartsWith("x:", StringComparison.Ordinal) || attribute.Name.StartsWith("xmlns", StringComparison.Ordinal) ||
                        attribute.Value.Contains("{") || PositioningAttributes.Contains(attribute.Name) || EventAttribute.IsMatch(attribute.Name))
                    {
                        continue;
                    }

                    if (pair.Value.All(u => u.Element.Attributes.Any(a => a.Name == attribute.Name && ValueEquals(a.Value, attribute.Value))))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(Descriptors.StyleWideAttribute, firstFile.LocationOf(attribute), pair.Value.Count, pair.Key, attribute.Name, attribute.Value));
                    }
                }
            }
        }

        /// <summary>A keyed element (a Style, a Thickness) by key: the page's own first, then App.xaml's and its merged dictionaries'.</summary>
        private static XamlElement KeyedElement(XamlFile file, XamlFile app, string localName, string key)
        {
            var own = file.Elements.FirstOrDefault(e => e.LocalName == localName && e.Value("x:Key") == key);
            return own ?? app?.Elements.FirstOrDefault(e => e.LocalName == localName && e.Value("x:Key") == key);
        }

        /// <summary>The value an attribute means: a {StaticResource} naming a Thickness resource is that resource's text.</summary>
        private static string Resolved(XamlFile file, XamlFile app, string value)
        {
            var m = StaticResourceValue.Match(value);
            return m.Success ? KeyedElement(file, app, "Thickness", m.Groups[1].Value)?.Text ?? value : value;
        }

        /// <summary>An element naming a keyed Style and then setting every one of its setter properties itself.</summary>
        private static void ReportOverriddenStyles(CompilationAnalysisContext context, XamlFile file,
            List<(string Key, string TargetType, List<(string Property, string Value)> Setters)> styles)
        {
            foreach (var element in file.Elements)
            {
                var styleKey = StaticResourceValue.Match(element.Value("Style") ?? string.Empty);
                if (!styleKey.Success)
                {
                    continue;
                }

                var style = styles.FirstOrDefault(s => s.Key == styleKey.Groups[1].Value && s.TargetType == element.LocalName);
                if (style.Key != null && style.Setters.All(s => element.Attributes.Any(a => a.Name == s.Property)))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Descriptors.StyleOverridden, file.LocationOf(element), element.Name, style.Key,
                        string.Join(", ", style.Setters.Select(s => s.Property))));
                }
            }
        }

        private static readonly string[] Panels =
        {
            "Grid", "StackPanel", "Canvas", "RelativePanel", "VariableSizedWrapGrid", "WrapGrid", "ItemsStackPanel", "ItemsWrapGrid"
        };

        /// <summary>
        ///     Grid.Row (and Column, spans) on an element whose parent is not a Grid; Canvas.ZIndex, Left
        ///     or Top on one whose parent is no Panel. A template's root and a property element's
        ///     content are skipped: their arranging parent is elsewhere.
        /// </summary>
        private static void ReportAttachedOutsidePanel(CompilationAnalysisContext context, XamlFile file, XamlElement element)
        {
            var parent = element.Parent;
            if (parent == null || parent.LocalName.Contains(".") || parent.LocalName.EndsWith("Template"))
            {
                return;
            }

            foreach (var attribute in element.Attributes)
            {
                var owner = attribute.Name.StartsWith("Grid.", StringComparison.Ordinal) ? "Grid"
                    : attribute.Name.StartsWith("Canvas.", StringComparison.Ordinal) ? "Panel"
                    : null;
                if (owner == "Grid" && parent.LocalName != "Grid" || owner == "Panel" && !Panels.Contains(parent.LocalName))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Descriptors.AttachedOutsidePanel, file.LocationOf(attribute), attribute.Name, owner, parent.LocalName));
                }
            }
        }

        /// <summary>Stretch on an axis whose size is fixed: the element sits at the default alignment instead.</summary>
        private static void ReportStretchWithFixedSize(CompilationAnalysisContext context, XamlFile file, XamlElement element)
        {
            foreach (var (alignment, size) in new[] { ("HorizontalAlignment", "Width"), ("VerticalAlignment", "Height") })
            {
                var attribute = element.Attribute(alignment);
                var fixedSize = element.Value(size);
                if (attribute != null && ValueEquals(attribute.Value, "Stretch") && fixedSize != null &&
                    !ValueEquals(fixedSize, "Auto") && !fixedSize.StartsWith("{", StringComparison.Ordinal))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Descriptors.StretchWithFixedSize, file.LocationOf(attribute), alignment, size, fixedSize));
                }
            }
        }

        private static readonly Regex GreyLiteral = new Regex(@"^(#[0-9A-Fa-f]{2}FFFFFF|#([0-9A-Fa-f]{2})\2\2|LightGray|Gray|DarkGray|Silver|Gainsboro|WhiteSmoke|DimGray)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static bool DimsByHand(string property, string value)
        {
            if (property == "Opacity")
            {
                return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var opacity) && opacity > 0 && opacity < 1;
            }

            return property == "Foreground" && GreyLiteral.IsMatch(value) &&
                   !value.Equals("#FFFFFFFF", StringComparison.OrdinalIgnoreCase) && !value.Equals("#FFFFFF", StringComparison.OrdinalIgnoreCase) &&
                   !value.Equals("#000000", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>A TextBlock (or a TextBlock Style's setter) dimmed with Opacity or a grey literal instead of the BaseMedium brush.</summary>
        private static void ReportTextDimmedByHand(CompilationAnalysisContext context, XamlFile file, XamlElement element)
        {
            if (element.LocalName == "TextBlock")
            {
                foreach (var attribute in element.Attributes.Where(a => DimsByHand(a.Name, a.Value)))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Descriptors.TextDimmedByHand, file.LocationOf(attribute), attribute.Name, attribute.Value));
                }
            }
            else if (element.LocalName == "Setter" && element.Parent?.Value("TargetType") == "TextBlock" &&
                     element.Value("Property") is string property && element.Value("Value") is string value && DimsByHand(property, value))
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.TextDimmedByHand, file.LocationOf(element), property, value));
            }
        }

        private const string AccentBrush = "{StaticResource SystemControlHighlightAccentBrush}";
        private const string TrackBrush = "{StaticResource SystemControlBackgroundBaseMediumLowBrush}";

        /// <summary>
        ///     A Rectangle filled with the accent (the value of a progress bar) and the Rectangles beside
        ///     it (its track): one height from the palette, one track brush, no Opacity.
        /// </summary>
        private static void ReportProgressBar(CompilationAnalysisContext context, XamlFile file, XamlElement element)
        {
            if (element.LocalName != "Rectangle" || element.Parent == null)
            {
                return;
            }

            var fill = element.Value("Fill") ?? string.Empty;
            var isValue = ValueEquals(fill, AccentBrush) || fill.Contains("SystemAccentColor");
            var siblings = element.Parent.Children.Where(c => c.LocalName == "Rectangle").ToList();
            var isTrack = !isValue && siblings.Any(s => ValueEquals(s.Value("Fill") ?? string.Empty, AccentBrush) || (s.Value("Fill") ?? string.Empty).Contains("SystemAccentColor"));
            if (!isValue && !isTrack)
            {
                return;
            }

            var faults = new List<string>();
            if (element.Value("Height") is string height && !ValueEquals(height, "{StaticResource ProgressBarHeight}"))
            {
                faults.Add("Height=\"" + height + "\"");
            }

            if (isValue && !ValueEquals(fill, AccentBrush))
            {
                faults.Add("Fill=\"" + fill + "\"");
            }

            if (isTrack && !ValueEquals(fill, TrackBrush))
            {
                faults.Add("Fill=\"" + fill + "\"");
            }

            if (element.Attribute("Opacity") != null)
            {
                faults.Add("Opacity=\"" + element.Value("Opacity") + "\"");
            }

            if (faults.Count > 0)
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.ProgressBarByHand, file.LocationOf(element), string.Join(", ", faults)));
            }
        }

        /// <summary>A Button whose Content is a glyph drawn with the icon font, where a FontIcon child is the form.</summary>
        private static void ReportGlyphButton(CompilationAnalysisContext context, XamlFile file, XamlElement element)
        {
            if (element.LocalName != "Button")
            {
                return;
            }

            var content = element.Value("Content") ?? string.Empty;
            var glyph = content.Length == 1 && content[0] >= '\uE000' && content[0] <= '\uF8FF';
            if (glyph || ValueEquals(element.Value("FontFamily") ?? string.Empty, "Segoe MDL2 Assets"))
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.GlyphButtonContent, file.LocationOf(element)));
            }
        }

        /// <summary>
        ///     A ProgressRing under a panel that has a Background and a bound Visibility: an overlay built
        ///     by hand. A panel that is not hit-test visible is a passive indicator (the player's buffering
        ///     sign), not the modal overlay, and is left alone.
        /// </summary>
        private static void ReportLoadingOverlaySpelledOut(CompilationAnalysisContext context, XamlFile file, XamlElement element)
        {
            if (element.LocalName != "ProgressRing" || file.FileName.Equals("LoadingOverlay.xaml", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            for (var panel = element.Parent; panel != null; panel = panel.Parent)
            {
                if (panel.Attribute("Background") != null && IsBound(panel.Value("Visibility") ?? string.Empty) &&
                    !ValueEquals(panel.Value("IsHitTestVisible") ?? "True", "False"))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Descriptors.LoadingOverlaySpelledOut, file.LocationOf(panel)));
                    return;
                }
            }
        }

        /// <summary>A Button whose content is a horizontal StackPanel of one icon and one TextBlock: one way to space and style them.</summary>
        private static void ReportIconLabelButton(CompilationAnalysisContext context, XamlFile file, XamlElement element)
        {
            if (element.LocalName != "StackPanel" || element.Parent?.LocalName != "Button" || !ValueEquals(element.Value("Orientation") ?? string.Empty, "Horizontal"))
            {
                return;
            }

            var children = element.Children;
            var icon = children.FirstOrDefault(c => c.LocalName == "FontIcon" || c.LocalName == "SymbolIcon");
            var label = children.FirstOrDefault(c => c.LocalName == "TextBlock");
            if (children.Count != 2 || icon == null || label == null)
            {
                return;
            }

            var faults = new List<string>();
            if (!ValueEquals(element.Value("Spacing") ?? string.Empty, "8"))
            {
                faults.Add("the panel's Spacing is " + (element.Value("Spacing") ?? "unset"));
            }

            foreach (var name in new[] { "Margin", "HorizontalAlignment", "VerticalAlignment" })
            {
                if (icon.Attribute(name) != null)
                {
                    faults.Add("the icon has " + name);
                }
            }

            foreach (var name in new[] { "Margin", "Style", "FontSize", "FontWeight", "Foreground", "VerticalAlignment" })
            {
                if (label.Attribute(name) != null)
                {
                    faults.Add("the label has " + name);
                }
            }

            if (faults.Count > 0)
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.IconLabelButton, file.LocationOf(element), string.Join(", ", faults)));
            }
        }

        /// <summary>A Rectangle's Fill or a Border's Background, solid, tinted with Opacity: the palette has the tint as a brush.</summary>
        private static void ReportFillDimmedByHand(CompilationAnalysisContext context, XamlFile file, XamlElement element)
        {
            var fillName = element.LocalName == "Rectangle" ? "Fill" : element.LocalName == "Border" ? "Background" : null;
            var fill = fillName == null ? null : element.Attribute(fillName);
            var opacity = element.Value("Opacity");
            if (fill == null || opacity == null || fill.Value.StartsWith("{Binding", StringComparison.Ordinal) ||
                fill.Value.StartsWith("{x:Bind", StringComparison.Ordinal) || !DimsByHand("Opacity", opacity))
            {
                return;
            }

            context.ReportDiagnostic(Diagnostic.Create(Descriptors.FillDimmedByHand, file.LocationOf(fill), fillName, fill.Value, opacity));
        }

        /// <summary>
        ///     A StackPanel with Spacing whose children (two or more) also carry a Margin along the panel's axis,
        ///     on the element or in its Style.
        /// </summary>
        private static void ReportMarginInsideSpacing(CompilationAnalysisContext context, XamlFile file, XamlFile app, XamlElement element)
        {
            var spacing = element.Value("Spacing");
            if (element.LocalName != "StackPanel" || spacing == null || ValueEquals(spacing, "0"))
            {
                return;
            }

            var horizontal = ValueEquals(element.Value("Orientation") ?? "Vertical", "Horizontal");
            var nudged = 0;
            foreach (var child in element.Children)
            {
                var margin = child.Value("Margin");
                if (margin == null)
                {
                    var styleKey = StaticResourceValue.Match(child.Value("Style") ?? string.Empty);
                    margin = styleKey.Success ? StyleSetters(file, app, styleKey.Groups[1].Value).FirstOrDefault(s => s.Property == "Margin").Value : null;
                }

                if (margin != null && AlongAxis(Resolved(file, app, margin), horizontal))
                {
                    nudged++;
                }
            }

            if (nudged > 1)
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.MarginInsideSpacing, file.LocationOf(element), nudged, spacing));
            }
        }

        /// <summary>
        ///     Width or Height beside its own bounds: Min and Max equal (that is the size), or the size
        ///     outside the Min or Max the element or its Style gives it (the bound wins).
        /// </summary>
        private static void ReportSizeAgainstBounds(CompilationAnalysisContext context, XamlFile file, XamlFile app, XamlElement element)
        {
            var isStyle = element.LocalName == "Style";
            var setters = isStyle
                ? element.Children.Where(c => c.LocalName == "Setter").ToDictionary(c => c.Value("Property") ?? string.Empty, c => c, StringComparer.Ordinal)
                : null;
            var styleKey = isStyle ? null : StaticResourceValue.Match(element.Value("Style") ?? string.Empty);
            var inherited = styleKey?.Success == true ? StyleSetters(file, app, styleKey.Groups[1].Value) : new List<(string Property, string Value)>();

            double? Size(string name, out object at)
            {
                at = null;
                var text = isStyle ? setters.TryGetValue(name, out var setter) ? setter.Value("Value") : null : element.Value(name);
                if (text != null)
                {
                    at = isStyle ? setters[name] : element.Attribute(name);
                }
                else if (!isStyle)
                {
                    text = inherited.FirstOrDefault(s => s.Property == name).Value;
                }

                return text != null && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
            }

            Location Where(object at) => at is XamlAttribute a ? file.LocationOf(a) : file.LocationOf((XamlElement)at);

            foreach (var axis in new[] { "Width", "Height" })
            {
                var size = Size(axis, out var sizeAt);
                var min = Size("Min" + axis, out var minAt);
                var max = Size("Max" + axis, out var maxAt);
                if (min != null && max != null && min == max && (minAt != null || maxAt != null))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Descriptors.SizeAgainstBounds, Where(minAt ?? maxAt),
                        "Min" + axis + " and Max" + axis + " are both " + min + ": that is " + axis + "=\"" + min + "\""));
                }

                if (size != null && sizeAt != null && ((min != null && size < min) || (max != null && size > max)))
                {
                    var bound = min != null && size < min ? "Min" + axis + " " + min : "Max" + axis + " " + max;
                    context.ReportDiagnostic(Diagnostic.Create(Descriptors.SizeAgainstBounds, Where(sizeAt),
                        axis + "=\"" + size + "\" is outside " + bound + ", which wins: the " + axis + " says nothing"));
                }
            }
        }

        /// <summary>
        ///     A StackPanel without Spacing whose first child has no Margin and whose every other child
        ///     has one that is one leading gap on the panel's axis and nothing else.
        /// </summary>
        private static void ReportSpacingByMargin(CompilationAnalysisContext context, XamlFile file, XamlElement element)
        {
            if (element.LocalName != "StackPanel" || element.Value("Spacing") != null || element.Children.Count < 3)
            {
                return;
            }

            var horizontal = ValueEquals(element.Value("Orientation") ?? "Vertical", "Horizontal");
            double? gap = null;
            for (var i = 0; i < element.Children.Count; i++)
            {
                var margin = element.Children[i].Value("Margin");
                if (i == 0 ? margin != null : margin == null)
                {
                    return;
                }

                if (i == 0)
                {
                    continue;
                }

                var sides = margin.Split(',').Select(s => double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN).ToList();
                if (sides.Count != 4 || sides.Any(double.IsNaN))
                {
                    return;
                }

                var leading = horizontal ? sides[0] : sides[1];
                var others = horizontal ? new[] { sides[1], sides[2], sides[3] } : new[] { sides[0], sides[2], sides[3] };
                if (leading <= 0 || others.Any(s => s != 0) || (gap != null && gap != leading))
                {
                    return;
                }

                gap = leading;
            }

            context.ReportDiagnostic(Diagnostic.Create(Descriptors.SpacingByMargin, file.LocationOf(element), gap));
        }

        /// <summary>Whether a Thickness has a non-zero side on the given axis (left/right or top/bottom).</summary>
        private static bool AlongAxis(string thickness, bool horizontal)
        {
            var sides = thickness.Split(',').Select(s => double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0).ToList();
            switch (sides.Count)
            {
                case 1: return sides[0] != 0;
                case 2: return sides[horizontal ? 0 : 1] != 0;
                case 4: return horizontal ? sides[0] != 0 || sides[2] != 0 : sides[1] != 0 || sides[3] != 0;
                default: return false;
            }
        }

        private static readonly string[] BrushAttributes = { "Background", "Foreground", "Fill", "BorderBrush", "Stroke" };

        /// <summary>A Color resource named where a Brush is expected.</summary>
        private static void ReportColourAsBrush(CompilationAnalysisContext context, XamlFile file, XamlElement element)
        {
            foreach (var attribute in element.Attributes.Where(a => BrushAttributes.Contains(a.Name)))
            {
                var m = Regex.Match(attribute.Value, @"^\{(?:Static|Theme)Resource\s+(\w+Color)\}$");
                if (m.Success)
                {
                    var brush = m.Groups[1].Value == "SystemAccentColor" ? "SystemControlHighlightAccentBrush" : "a brush of that colour";
                    context.ReportDiagnostic(Diagnostic.Create(Descriptors.ColourAsBrush, file.LocationOf(attribute), attribute.Name, attribute.Value, brush));
                }
            }
        }

        private static readonly Regex HexColour = new Regex(@"^#[0-9A-Fa-f]{6}([0-9A-Fa-f]{2})?$", RegexOptions.Compiled);

        /// <summary>A hex colour anywhere but a keyed Color or SolidColorBrush definition.</summary>
        private static void ReportColourLiteral(CompilationAnalysisContext context, XamlFile file, XamlElement element)
        {
            if ((element.LocalName == "Color" || element.LocalName == "SolidColorBrush") && element.Attribute("x:Key") != null)
            {
                return;
            }

            // A named colour (Red, Gold) on a brush attribute is the same literal in words; White is BaseHigh
            // (GEL015, GEL067) and Transparent is the one colour that names no palette entry
            bool Named(XamlAttribute a) => (BrushAttributes.Contains(a.Name) || a.Name == "Color") && NamedColour.IsMatch(a.Value) &&
                                           !ValueEquals(a.Value, "Transparent") && !WhiteForegrounds.Contains(a.Value, StringComparer.OrdinalIgnoreCase);
            foreach (var attribute in element.Attributes.Where(a => HexColour.IsMatch(a.Value) || Named(a)))
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.ColourLiteral, file.LocationOf(attribute), attribute.Name, attribute.Value));
            }
        }

        private static readonly Regex NamedColour = new Regex(@"^[A-Za-z]+$", RegexOptions.Compiled);

        /// <summary>A page's root Background that is not the theme brush.</summary>
        private static void ReportPageBackground(CompilationAnalysisContext context, XamlFile file)
        {
            var root = file.Elements.FirstOrDefault();
            var background = root?.LocalName.EndsWith("Page", StringComparison.Ordinal) == true ? root.Attribute("Background") : null;
            // The player is black: the bars around video that does not fill the screen are its background
            var expected = file.FileName.Equals("MediaPlayerPage.xaml", StringComparison.OrdinalIgnoreCase)
                ? "{StaticResource VideoBackgroundBrush}"
                : "{StaticResource ApplicationPageBackgroundThemeBrush}";
            if (background != null && !ValueEquals(background.Value, expected))
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.PageBackground, file.LocationOf(background), background.Value));
            }
        }

        private static readonly Regex BindOneTime = new Regex(@"\{x:Bind\b[^}]*\bMode=OneTime\b", RegexOptions.Compiled);

        /// <summary>x:DefaultBindMode on a page, or Mode=OneTime where OneTime is already what a bare x:Bind means.</summary>
        private static void ReportBindMode(CompilationAnalysisContext context, XamlFile file)
        {
            var defaultMode = file.Elements.SelectMany(e => e.Attributes).FirstOrDefault(a => a.Name == "x:DefaultBindMode");
            if (defaultMode != null)
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.BindModeConvention, file.LocationOf(defaultMode),
                    "x:DefaultBindMode changes what a bare x:Bind means on this page; each binding states its own Mode instead"));
                return;
            }

            foreach (var attribute in file.Elements.SelectMany(e => e.Attributes).Where(a => BindOneTime.IsMatch(a.Value)))
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.BindModeConvention, file.LocationOf(attribute),
                    "Mode=OneTime is what a bare x:Bind already means: leave the Mode off"));
            }
        }

        /// <summary>A file's only TabIndex: with nothing to order against, it orders nothing.</summary>
        private static void ReportLoneTabIndex(CompilationAnalysisContext context, XamlFile file)
        {
            var tabIndexes = file.Elements.SelectMany(e => e.Attributes.Where(a => a.Name == "TabIndex")).ToList();
            if (tabIndexes.Count == 1)
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.LoneTabIndex, file.LocationOf(tabIndexes[0])));
            }
        }

        /// <summary>An element without a Style that carries every setter of a keyed Style for its type.</summary>
        private static void ReportSpelledOutStyles(CompilationAnalysisContext context, XamlFile file,
            List<(string Key, string TargetType, List<(string Property, string Value)> Setters)> styles)
        {
            foreach (var element in file.Elements)
            {
                if (element.Attribute("Style") != null || element.LocalName == "Setter")
                {
                    continue;
                }

                foreach (var style in styles.Where(s => s.TargetType == element.LocalName))
                {
                    // A TextBlock's default text brush is BaseHigh, so a Foreground setter at BaseHigh is met by no attribute at all
                    bool Implied((string Property, string Value) s) => element.LocalName == "TextBlock" && s.Property == "Foreground" &&
                                                                        WhiteForegrounds.Contains(s.Value, StringComparer.OrdinalIgnoreCase) && element.Attribute("Foreground") == null;
                    if (style.Setters.Any(s => !Implied(s)) &&
                        style.Setters.All(s => Implied(s) || element.Attributes.Any(a => a.Name == s.Property && SameValue(a.Value, s.Value))))
                    {
                        var spelled = string.Join(", ", style.Setters.Select(s => s.Property + "=\"" + s.Value + "\""));
                        context.ReportDiagnostic(Diagnostic.Create(Descriptors.StyleSpelledOut, file.LocationOf(element), element.Name, style.Key, spelled));
                        break;
                    }
                }
            }
        }

        private static readonly Regex MarkupPrefix = new Regex(@"(?<=[\s{=,(])([A-Za-z_]\w*):(?=[A-Za-z_])", RegexOptions.Compiled);

        /// <summary>A prefix inside a {markup extension} that no xmlns on the root declares.</summary>
        private static void ReportUndeclaredPrefixes(CompilationAnalysisContext context, XamlFile file)
        {
            var declared = new HashSet<string>(file.Root.Attributes.Where(a => a.Name.StartsWith("xmlns:", StringComparison.Ordinal))
                .Select(a => a.Name.Substring("xmlns:".Length)), StringComparer.Ordinal);
            foreach (var element in file.Elements)
            {
                foreach (var attribute in element.Attributes)
                {
                    if (!attribute.Value.StartsWith("{", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    foreach (Match m in MarkupPrefix.Matches(attribute.Value))
                    {
                        if (!declared.Contains(m.Groups[1].Value))
                        {
                            context.ReportDiagnostic(Diagnostic.Create(Descriptors.MarkupPrefixUndeclared, file.LocationOf(attribute), m.Groups[1].Value, attribute.Name));
                        }
                    }
                }
            }
        }

        /// <summary>A d: attribute or mc:Ignorable: the designer's, and this project never opens one.</summary>
        private static void ReportDesignTime(CompilationAnalysisContext context, XamlFile file)
        {
            foreach (var attribute in file.Elements.SelectMany(e => e.Attributes))
            {
                if (attribute.Name.StartsWith("d:", StringComparison.Ordinal) || attribute.Name == "mc:Ignorable")
                {
                    context.ReportDiagnostic(Diagnostic.Create(Descriptors.DesignTimeAttribute, file.LocationOf(attribute), attribute.Name));
                }
            }
        }

        private static void ReportXmlns(CompilationAnalysisContext context, XamlFile file)
        {
            foreach (var attribute in file.Root.Attributes.Where(a => a.Name.StartsWith("xmlns:", StringComparison.Ordinal)))
            {
                var prefix = attribute.Name.Substring("xmlns:".Length);
                var declaration = "xmlns:" + prefix + "=\"" + attribute.Value + "\"";
                var body = file.RawText.Replace(declaration, string.Empty);
                if (!Regex.IsMatch(body, @"[<\s""{,(]" + Regex.Escape(prefix) + ":"))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Descriptors.XmlnsPrefixUnused, file.LocationOf(attribute), prefix));
                }
            }
        }

        private static void ReportNamedElement(CompilationAnalysisContext context, XamlFile file, XamlElement element,
            HashSet<string> pageIdentifiers, CodeIndex code)
        {
            var name = element.Attribute("x:Name");
            if (name == null || element.InTemplate || element.LocalName == "VisualState" || element.LocalName == "VisualStateGroup")
            {
                return;
            }

            if (file.MentionsElsewhere(name.Value) || pageIdentifiers.Contains(name.Value) || code.StringLiteralExists(name.Value, null))
            {
                return;
            }

            context.ReportDiagnostic(Diagnostic.Create(Descriptors.NamedElementNeverAddressed, file.LocationOf(name), name.Value));
        }

        private static void ReportLoadingOverlay(CompilationAnalysisContext context, XamlFile file, XamlElement element, HashSet<string> pageIdentifiers)
        {
            if (element.LocalName != "LoadingOverlay" || file.FileName.Equals("LoadingOverlay.xaml", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (element.Attribute("IsLoading") != null)
            {
                return;
            }

            if (element.Attribute("x:Name") != null && pageIdentifiers.Contains("IsLoading"))
            {
                return;
            }

            context.ReportDiagnostic(Diagnostic.Create(Descriptors.LoadingOverlayUnbound, file.LocationOf(element)));
        }

        private static void ReportDefaults(CompilationAnalysisContext context, XamlFile file, XamlFile app, XamlElement element)
        {
            if (element.LocalName == "Setter")
            {
                ReportSetterAtDefault(context, file, app, element);
                return;
            }

            var hasStyle = element.Attribute("Style") != null;
            var styleKey = StaticResourceValue.Match(element.Value("Style") ?? string.Empty);
            var styled = styleKey.Success ? StyleSetters(file, app, styleKey.Groups[1].Value).Select(s => s.Property).ToList() : new List<string>();
            foreach (var attribute in element.Attributes)
            {
                if (element.LocalName == "ScrollViewer" && attribute.Name.StartsWith("ScrollViewer.", StringComparison.Ordinal))
                {
                    var own = attribute.Name.Substring("ScrollViewer.".Length);
                    context.ReportDiagnostic(Diagnostic.Create(Descriptors.AttachedFormOnOwner, file.LocationOf(attribute),
                        attribute.Name, element.Name, own + "=\"" + attribute.Value + "\""));
                    continue;
                }

                foreach (var (name, value, elements) in Defaults)
                {
                    if (attribute.Name != name || !SameValue(Resolved(file, app, attribute.Value), value))
                    {
                        continue;
                    }

                    if (!elements.Contains("*") && !elements.Contains(element.LocalName))
                    {
                        continue;
                    }

                    // A RowDefinition's Height and a ColumnDefinition's Width default to a star, not Auto
                    if ((name == "Width" || name == "Height") && element.LocalName.EndsWith("Definition", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    // A Style may move these (an attribute repeating the Style is GEL067's); one putting back
                    // the default over what its Style set is deliberate
                    if ((hasStyle && (name == "Margin" || name == "Padding")) || styled.Contains(name))
                    {
                        continue;
                    }

                    // BasePage enables XY keyboard navigation on every page, and descendants inherit it;
                    // RootContainer's Grid is the one place that sets it for what lies outside a page
                    if (name == "XYFocusKeyboardNavigation" && file.FileName.Equals("RootContainer.xaml", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    context.ReportDiagnostic(Diagnostic.Create(Descriptors.AttributeAtDefault, file.LocationOf(attribute),
                        attribute.Name, attribute.Value, element.Name));
                }
            }
        }

        private static void ReportSetterAtDefault(CompilationAnalysisContext context, XamlFile file, XamlFile app, XamlElement setter)
        {
            var style = setter.Parent;
            var target = style?.LocalName == "Style" ? style.Value("TargetType") : null;
            var property = setter.Value("Property");
            var value = setter.Value("Value");
            if (target == null || property == null || value == null)
            {
                return;
            }

            // A derived Style may set a property back to the default its base moved; a base that
            // cannot be found (a platform style) is taken to have moved it
            for (var basedOn = style.Value("BasedOn"); basedOn != null;)
            {
                var m = StaticResourceValue.Match(basedOn);
                var baseStyle = m.Success ? KeyedElement(file, app, "Style", m.Groups[1].Value) : null;
                if (baseStyle == null || baseStyle.Children.Any(c => c.LocalName == "Setter" && c.Value("Property") == property))
                {
                    return;
                }

                basedOn = baseStyle.Value("BasedOn");
            }

            foreach (var (name, expected, elements) in Defaults)
            {
                if (property == name && SameValue(Resolved(file, app, value), expected) && (elements.Contains("*") || elements.Contains(target)))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Descriptors.AttributeAtDefault, file.LocationOf(setter), property, value, target));
                }
            }
        }

        /// <summary>A property element holding nothing but a value is the attribute written long.</summary>
        private static void ReportPropertyElementText(CompilationAnalysisContext context, XamlFile file, XamlElement element)
        {
            foreach (var property in element.PropertyElements)
            {
                var attribute = property.LocalName.Substring(property.LocalName.IndexOf('.') + 1);
                if (property.Text != null && property.Children.Count == 0 && property.PropertyElements.Count == 0)
                {
                    context.ReportDiagnostic(Diagnostic.Create(Descriptors.PropertyElementAsText, file.LocationOf(property), property.Name, property.Text, attribute));
                }
                else if (property.Text == null && property.Children.Count == 1 && property.Children[0].LocalName == "StaticResource" &&
                         property.Children[0].Value("ResourceKey") is string key && property.Children[0].Attributes.Count == 1)
                {
                    context.ReportDiagnostic(Diagnostic.Create(Descriptors.PropertyElementAsResource, file.LocationOf(property), property.Name, key, attribute));
                }
            }
        }

        private static bool ValueEquals(string actual, string expected)
        {
            if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // a Thickness of four zeros is 0
            return expected == "0" && Regex.IsMatch(actual, @"^\s*0(\s*,\s*0){1,3}\s*$");
        }

        /// <summary>
        ///     Elements of one type that spell out the same styling attributes: three sharing three or
        ///     more, four sharing two, or two sharing five or more. Bound values are left out; they differ per element. A
        ///     resource reference counts: the same {StaticResource} on each is as spelled out as a literal.
        /// </summary>
        private static void ReportRepeatedAttributeSets(CompilationAnalysisContext context, XamlFile file)
        {
            var groups = new Dictionary<string, List<XamlElement>>(StringComparer.Ordinal);
            foreach (var element in file.Elements)
            {
                if (element.LocalName.Contains(".") || element.LocalName == "Setter")
                {
                    continue;
                }

                var pairs = element.Attributes.Where(a => StylingAttributes.Contains(a.Name) && !IsBound(a.Value))
                    .OrderBy(a => a.Name, StringComparer.Ordinal).Select(a => a.Name + "=\"" + a.Value + "\"").ToList();
                if (pairs.Count < 2)
                {
                    continue;
                }

                var key = element.LocalName + "|" + string.Join(" ", pairs);
                if (!groups.TryGetValue(key, out var list))
                {
                    groups[key] = list = new List<XamlElement>();
                }

                list.Add(element);
            }

            foreach (var pair in groups)
            {
                var attributes = pair.Key.Substring(pair.Key.IndexOf('|') + 1);
                // Not a split on spaces: a {StaticResource Key} value holds one
                var count = Regex.Matches(attributes, "=\"").Count;
                if ((count >= 3 && pair.Value.Count >= 3) || (count == 2 && pair.Value.Count >= 4) || (count >= 5 && pair.Value.Count >= 2))
                {
                    var first = pair.Value.OrderBy(e => e.Line).First();
                    context.ReportDiagnostic(Diagnostic.Create(Descriptors.RepeatedAttributeSet, file.LocationOf(first), pair.Value.Count, first.Name, attributes));
                }
            }
        }

        /// <summary>A Thickness in its shortest form: one value for four equal sides, two for left=right and top=bottom.</summary>
        private static void ReportThickness(CompilationAnalysisContext context, XamlFile file, XamlElement element)
        {
            foreach (var attribute in element.Attributes)
            {
                var isThickness = ThicknessAttributes.Contains(attribute.Name) ||
                                  (element.LocalName == "Setter" && attribute.Name == "Value" && ThicknessAttributes.Contains(element.Value("Property") ?? string.Empty));
                if (!isThickness)
                {
                    continue;
                }

                var shortForm = ShortThickness(attribute.Value);
                if (shortForm != null && shortForm != attribute.Value)
                {
                    context.ReportDiagnostic(Diagnostic.Create(Descriptors.ThicknessWrittenLong, file.LocationOf(attribute), attribute.Value, shortForm));
                }
            }

            if (element.LocalName.EndsWith("Definition", StringComparison.Ordinal))
            {
                var length = element.Attribute("Width") ?? element.Attribute("Height");
                if (length != null && length.Value.Trim() == "1*")
                {
                    context.ReportDiagnostic(Diagnostic.Create(Descriptors.ThicknessWrittenLong, file.LocationOf(length), length.Value, "*"));
                }
            }
        }

        // One colour on the console's dark theme; the brush is the one to keep
        private static readonly string[] WhiteForegrounds = { "White", "#FFFFFFFF", "#FFFFFF", "{StaticResource SystemControlForegroundBaseHighBrush}", "{ThemeResource SystemControlForegroundBaseHighBrush}" };

        private static bool SameValue(string actual, string expected)
        {
            return ValueEquals(actual, expected) ||
                   (WhiteForegrounds.Contains(actual, StringComparer.OrdinalIgnoreCase) && WhiteForegrounds.Contains(expected, StringComparer.OrdinalIgnoreCase));
        }

        /// <summary>The setters a keyed Style gives an element, its BasedOn chain followed, the nearest Style's value winning.</summary>
        private static List<(string Property, string Value)> StyleSetters(XamlFile file, XamlFile app, string key)
        {
            var setters = new List<(string, string)>();
            var seen = new HashSet<string>();
            for (var style = KeyedElement(file, app, "Style", key); style != null && seen.Add(key);)
            {
                foreach (var setter in style.Children.Where(c => c.LocalName == "Setter" && c.Value("Property") != null && c.Value("Value") != null))
                {
                    if (setters.All(s => s.Item1 != setter.Value("Property")))
                    {
                        setters.Add((setter.Value("Property"), setter.Value("Value")));
                    }
                }

                var basedOn = StaticResourceValue.Match(style.Value("BasedOn") ?? string.Empty);
                if (!basedOn.Success)
                {
                    break;
                }

                key = basedOn.Groups[1].Value;
                style = KeyedElement(file, app, "Style", key);
            }

            return setters;
        }

        /// <summary>An attribute whose value the element's own Style already sets.</summary>
        private static void ReportAttributeRepeatsStyle(CompilationAnalysisContext context, XamlFile file, XamlFile app, XamlElement element)
        {
            var styleKey = StaticResourceValue.Match(element.Value("Style") ?? string.Empty);
            if (!styleKey.Success)
            {
                return;
            }

            var setters = StyleSetters(file, app, styleKey.Groups[1].Value);
            foreach (var attribute in element.Attributes)
            {
                var setter = setters.FirstOrDefault(s => s.Property == attribute.Name);
                if (setter.Property != null && SameValue(attribute.Value, setter.Value))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Descriptors.AttributeRepeatsStyle, file.LocationOf(attribute), attribute.Name, attribute.Value, styleKey.Groups[1].Value, element.Name));
                }
            }
        }

        private static string ShortThickness(string value)
        {
            var parts = value.Split(',').Select(p => p.Trim()).ToArray();
            if (parts.Any(p => p.Length == 0 || p.Contains("{")))
            {
                return null;
            }

            if (parts.Length == 4 && parts[0] == parts[2] && parts[1] == parts[3])
            {
                parts = new[] { parts[0], parts[1] };
            }

            if (parts.Length == 2 && parts[0] == parts[1])
            {
                parts = new[] { parts[0] };
            }

            return string.Join(",", parts);
        }

        private static void ReportWrapper(CompilationAnalysisContext context, XamlFile file, XamlElement element)
        {
            if (!WrapperCandidates.Contains(element.LocalName) || element.Children.Count != 1 || element.PropertyElements.Count > 0)
            {
                return;
            }

            var child = element.Children[0];
            var passThrough = PassThroughWrappers.Contains(element.LocalName);
            if (element.Attributes.Any(a => !PositioningAttributes.Contains(a.Name) &&
                                            !(passThrough && PassThroughAttributes.Contains(a.Name) && (child.Value(a.Name) ?? a.Value) == a.Value)))
            {
                return;
            }

            context.ReportDiagnostic(Diagnostic.Create(Descriptors.WrapperElement, file.LocationOf(element), element.Name));
        }

        private static void ReportGridSlots(CompilationAnalysisContext context, XamlFile file, XamlElement element)
        {
            if (element.LocalName != "Grid")
            {
                return;
            }

            ReportSlots(context, file, element, "Grid.RowDefinitions", "RowDefinition", "Grid.Row", "Grid.RowSpan", "Row");
            ReportSlots(context, file, element, "Grid.ColumnDefinitions", "ColumnDefinition", "Grid.Column", "Grid.ColumnSpan", "Column");
        }

        private static void ReportSlots(CompilationAnalysisContext context, XamlFile file, XamlElement grid,
            string definitionsName, string definitionName, string indexAttribute, string spanAttribute, string label)
        {
            var definitions = grid.PropertyElements.FirstOrDefault(p => p.Name == definitionsName);
            if (definitions == null)
            {
                return;
            }

            var count = definitions.Children.Count(d => d.LocalName == definitionName);
            if (count < 2 || grid.Children.Count == 0)
            {
                return;
            }

            // A fixed or star slot with nothing in it is spacing; only an Auto slot collapses to nothing
            var covered = new bool[count];
            var sizes = definitions.Children.Where(d => d.LocalName == definitionName).Select(d => d.Value(label == "Row" ? "Height" : "Width") ?? "*").ToList();
            for (var i = 0; i < count; i++)
            {
                covered[i] = !string.Equals(sizes[i], "Auto", StringComparison.OrdinalIgnoreCase);
            }

            foreach (var child in grid.Children)
            {
                var indexText = child.Value(indexAttribute) ?? "0";
                var spanText = child.Value(spanAttribute) ?? "1";
                if (!int.TryParse(indexText, out var index) || !int.TryParse(spanText, out var span))
                {
                    return; // bound or otherwise dynamic placement: nothing to say
                }

                for (var i = index; i < Math.Min(count, index + span); i++)
                {
                    covered[i] = true;
                }
            }

            for (var i = 0; i < count; i++)
            {
                if (!covered[i])
                {
                    var definition = definitions.Children.Where(d => d.LocalName == definitionName).ElementAt(i);
                    context.ReportDiagnostic(Diagnostic.Create(Descriptors.GridSlotUnused, file.LocationOf(definition), label, i));
                }
            }
        }

        private static void ReportBinding(CompilationAnalysisContext context, XamlFile file, XamlElement element)
        {
            if (file.ClassName == null || !file.UsesXBind || element.InTemplate)
            {
                return;
            }

            foreach (var attribute in element.Attributes)
            {
                if (attribute.Value.TrimStart().StartsWith("{Binding", StringComparison.Ordinal))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Descriptors.BindingAmongXBind, file.LocationOf(attribute)));
                }
            }
        }

        /// <summary>
        ///     What the code side offers the XAML rules: the identifiers a page's class (and its bases)
        ///     use, and the string literals anywhere.
        /// </summary>
        private sealed class CodeIndex
        {
            private readonly Compilation _compilation;
            private readonly HashSet<string> _allLiterals = new HashSet<string>(StringComparer.Ordinal);
            private readonly Dictionary<SyntaxTree, HashSet<string>> _literalsByTree = new Dictionary<SyntaxTree, HashSet<string>>();
            private readonly Dictionary<SyntaxTree, HashSet<string>> _identifiersByTree = new Dictionary<SyntaxTree, HashSet<string>>();

            public CodeIndex(Compilation compilation)
            {
                _compilation = compilation;
                foreach (var tree in compilation.SyntaxTrees)
                {
                    if (IsGenerated(tree))
                    {
                        continue;
                    }

                    var literals = new HashSet<string>(StringComparer.Ordinal);
                    var identifiers = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var token in tree.GetRoot().DescendantTokens())
                    {
                        if (token.IsKind(SyntaxKind.StringLiteralToken))
                        {
                            literals.Add(token.ValueText);
                            _allLiterals.Add(token.ValueText);
                        }
                        else if (token.IsKind(SyntaxKind.IdentifierToken))
                        {
                            identifiers.Add(token.ValueText);
                        }
                    }

                    _literalsByTree[tree] = literals;
                    _identifiersByTree[tree] = identifiers;
                }
            }

            public static bool IsGenerated(SyntaxTree tree)
            {
                var path = tree.FilePath ?? string.Empty;
                return path.IndexOf("\\obj\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       path.IndexOf("/obj/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
                       path.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase) ||
                       string.IsNullOrEmpty(path);
            }

            /// <summary>Identifiers used in the partial class named <paramref name="className" /> and its base classes.</summary>
            public HashSet<string> IdentifiersOfClassAndBases(string className)
            {
                var result = new HashSet<string>(StringComparer.Ordinal);
                if (className == null)
                {
                    return result;
                }

                foreach (var symbol in _compilation.GetSymbolsWithName(className, SymbolFilter.Type).OfType<INamedTypeSymbol>())
                {
                    for (var type = symbol; type != null; type = type.BaseType)
                    {
                        foreach (var reference in type.DeclaringSyntaxReferences)
                        {
                            if (_identifiersByTree.TryGetValue(reference.SyntaxTree, out var identifiers))
                            {
                                result.UnionWith(identifiers);
                            }
                        }
                    }
                }

                return result;
            }

            /// <summary>A string literal equal to <paramref name="value" /> in the given classes' files, or anywhere when <paramref name="classNames" /> is null.</summary>
            public bool StringLiteralExists(string value, IEnumerable<string> classNames)
            {
                if (classNames == null)
                {
                    return _allLiterals.Contains(value);
                }

                foreach (var className in classNames.Where(c => c != null))
                {
                    foreach (var symbol in _compilation.GetSymbolsWithName(className, SymbolFilter.Type).OfType<INamedTypeSymbol>())
                    {
                        foreach (var reference in symbol.DeclaringSyntaxReferences)
                        {
                            if (_literalsByTree.TryGetValue(reference.SyntaxTree, out var literals) && literals.Contains(value))
                            {
                                return true;
                            }
                        }
                    }
                }

                return false;
            }
        }
    }
}
