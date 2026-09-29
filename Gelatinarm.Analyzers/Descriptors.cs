using Microsoft.CodeAnalysis;

namespace Gelatinarm.Analyzers
{
    /// <summary>
    ///     Every rule's id, title and message. Ids are GEL + three digits; each is a warning, and
    ///     the build's baseline is no warning, so any of them fails the build gate.
    /// </summary>
    internal static class Descriptors
    {
        private const string DeadCode = "Gelatinarm.DeadCode";
        private const string Consistency = "Gelatinarm.Consistency";
        private const string Xaml = "Gelatinarm.Xaml";
        private const string Safety = "Gelatinarm.Safety";

        private static DiagnosticDescriptor Rule(string id, string category, string title, string message,
            string description = null)
        {
            return new DiagnosticDescriptor(id, title, message, category, DiagnosticSeverity.Warning,
                isEnabledByDefault: true, description: description);
        }

        // Members and their uses, counted by symbol with XAML references included
        public static readonly DiagnosticDescriptor MemberNeverReferenced = Rule("GEL001", DeadCode,
            "Member is never referenced",
            "{0} '{1}' is never referenced (code, XAML and rd.xml included)",
            "In a single-assembly app a public member nothing references is dead. Overrides, interface implementations, XAML event handlers and generated-command sources count as referenced when the interface member, the XAML or the command is.");

        public static readonly DiagnosticDescriptor MemberOnlyUsedInside = Rule("GEL002", DeadCode,
            "Public or protected member is used only inside its own type",
            "{0} '{1}' is {2} but only its own type uses it: make it private",
            "A helper that looks like shared API invites a second caller to reach past the type's own flow.");

        public static readonly DiagnosticDescriptor MemberWrittenNeverRead = Rule("GEL003", DeadCode,
            "Member is written but never read",
            "{0} '{1}' is assigned but nothing reads it",
            "An assignment is not a use. Bindings in XAML count as reads.");

        public static readonly DiagnosticDescriptor ObservableStateNeverSet = Rule("GEL004", DeadCode,
            "Observable property is never assigned",
            "'{0}' is bound or read but nothing assigns it, so it keeps its default forever",
            "A value that never changes is a constant or a XAML literal; one that should change is unwired. A TwoWay binding counts as an assignment.");

        public static readonly DiagnosticDescriptor FieldOnlyNullChecked = Rule("GEL005", DeadCode,
            "Field is only ever compared to null",
            "'{0}' is read only in null checks: it holds nothing anyone uses");

        public static readonly DiagnosticDescriptor InterfaceMemberOnlyImplementationUses = Rule("GEL006", DeadCode,
            "Interface member is used only by its implementation",
            "'{0}.{1}' is called only inside the class that implements it: it does not belong on the interface");

        public static readonly DiagnosticDescriptor EmptyVirtualHookNeverOverridden = Rule("GEL007", DeadCode,
            "Empty virtual hook nothing overrides",
            "'{0}' is virtual with an empty body and no override: the hook is unused");

        public static readonly DiagnosticDescriptor StateRedeclaredFromBase = Rule("GEL008", DeadCode,
            "State redeclared from a base class",
            "'{0}.{1}' redeclares storage its base class '{2}' already holds");

        public static readonly DiagnosticDescriptor NullCheckOnTrustedField = Rule("GEL009", Consistency,
            "Null check on storage that cannot be null",
            "'{0}' is set from a constructor argument, a required service or a new object and cannot be null here",
            "Constructor arguments come from the DI container, which fails a resolution it cannot satisfy; GetRequiredService throws; new returns an object. A null check on such readonly storage can never fire.");

        // XAML
        public static readonly DiagnosticDescriptor ResourceKeyNeverReferenced = Rule("GEL010", Xaml,
            "Resource key is never referenced in its scope",
            "x:Key '{0}' is referenced nowhere it is visible",
            "A page's resources are visible only to that page (and its code-behind); a merged dictionary's to the pages that merge it; App.xaml's (and its merged dictionaries') everywhere.");

        public static readonly DiagnosticDescriptor StaticResourceUndefined = Rule("GEL011", Xaml,
            "StaticResource key is defined nowhere",
            "{{StaticResource {0}}} has no x:Key in this app's XAML; the page fails on the console, not at build");

        public static readonly DiagnosticDescriptor NamedElementNeverAddressed = Rule("GEL012", Xaml,
            "Named element is addressed by nothing",
            "x:Name '{0}' is used by neither its page's code nor its XAML");

        public static readonly DiagnosticDescriptor LoadingOverlayUnbound = Rule("GEL013", Xaml,
            "Loading overlay nothing turns on",
            "This LoadingOverlay has no IsLoading binding and no code sets it, so it never shows");

        public static readonly DiagnosticDescriptor XmlnsPrefixUnused = Rule("GEL014", Xaml,
            "XML namespace prefix is unused",
            "xmlns:{0} is declared but nothing in the file uses it");

        public static readonly DiagnosticDescriptor AttributeAtDefault = Rule("GEL015", Xaml,
            "Attribute set to the element's default",
            "{0}=\"{1}\" is what a {2} already has (property default or default style); it says nothing",
            "The table of defaults comes from the SDK's generic.xaml: a Button's style sets HorizontalAlignment=Left, a ListView's IsTabStop=False, so on those the same attribute means something and is not reported. A Setter in a Style is checked against its TargetType the same way, unless a Style in its BasedOn chain sets the property. A {StaticResource} naming a Thickness resource counts as that resource's value.");

        public static readonly DiagnosticDescriptor AttachedFormOnOwner = Rule("GEL016", Xaml,
            "Attached-property form used on the owning element",
            "{0} on a {1} is the element's own property: write {2}");

        public static readonly DiagnosticDescriptor WrapperElement = Rule("GEL017", Xaml,
            "Element wraps a single child and sets nothing",
            "This {0} holds one child and nothing of its own the child could not carry: the child can stand here itself");

        public static readonly DiagnosticDescriptor GridSlotUnused = Rule("GEL018", Xaml,
            "Grid row or column has no content",
            "{0} {1} is defined but no child is placed in it");

        public static readonly DiagnosticDescriptor BindingAmongXBind = Rule("GEL019", Xaml,
            "Binding used where the page binds with x:Bind",
            "{{Binding}} in a page that binds with x:Bind: use x:Bind, which is checked at compile time");

        public static readonly DiagnosticDescriptor ThicknessWrittenLong = Rule("GEL040", Xaml,
            "Thickness or grid length written in a longer form than it needs",
            "{0} is {1}: one value for four equal sides, two for symmetric ones; a star needs no 1");

        public static readonly DiagnosticDescriptor RepeatedAttributeSet = Rule("GEL042", Xaml,
            "The same styling attributes on several elements",
            "{0} {1} elements in this file set {2}: a Style holds that once",
            "Width, colours, font, padding and the like written out on each element are a Style spelled by hand; the count is elements of one type carrying the same set (three sharing three, four sharing two, or two sharing five: the poster templates' titles).");

        public static readonly DiagnosticDescriptor PropertyElementAsText = Rule("GEL046", Xaml,
            "Property element holding a plain value",
            "<{0}> holds only \"{1}\": the attribute {2}=\"{1}\" says the same in one line",
            "Property-element syntax is for values that need an element (definitions, templates, brushes); a number or word in it is the attribute form written long.");

        // Async and error handling
        public static readonly DiagnosticDescriptor AsyncWithoutAwait = Rule("GEL020", Consistency,
            "Async method or lambda never awaits",
            "'{0}' is async but awaits nothing: it runs synchronously to completion, with the async wrapper's cost and none of its meaning");

        public static readonly DiagnosticDescriptor TaskMethodNeverWaits = Rule("GEL021", Consistency,
            "Task-returning method never waits",
            "'{0}' returns only Task.CompletedTask: it is a synchronous method in a Task signature");

        public static readonly DiagnosticDescriptor SyncOverAsync = Rule("GEL022", Safety,
            "Synchronous wait on a task",
            "'{0}' blocks on a task; on the UI thread this deadlocks");

        public static readonly DiagnosticDescriptor CatchReportsThenRethrows = Rule("GEL023", Consistency,
            "Catch reports the error, then rethrows it",
            "This catch logs or handles the exception and then throws it on: the caller reports it again. Report once, where it is handled");

        public static readonly DiagnosticDescriptor CredentialLogged = Rule("GEL024", Safety,
            "Credential in a log argument",
            "'{0}' names a secret, token, password or API key: log its presence, not its value");

        public static readonly DiagnosticDescriptor StreamUrlLogged = Rule("GEL025", Safety,
            "Stream URL logged without redaction",
            "'{0}' carries the access token as ApiKey: pass it through UrlHelper.RedactApiKey");

        public static readonly DiagnosticDescriptor TokenSourceReplacedByHand = Rule("GEL026", Consistency,
            "Cancellation token source cancelled and replaced or disposed by hand",
            "'{0}' is cancelled and {1} by hand: AsyncHelper.{2} does that, with its atomic swap and the dispose",
            "Cancel-then-new is Supersede; cancel-then-dispose (and null) is Cancel. Written out, a site forgets the dispose, the swap or the disposed-source case.");

        public static readonly DiagnosticDescriptor ExceptionMessageRuleOrphaned = Rule("GEL027", DeadCode,
            "Exception filter matches a message nothing throws",
            "No string in the code contains \"{0}\": this filter can never match");

        public static readonly DiagnosticDescriptor OptionalParameterNeverPassed = Rule("GEL028", DeadCode,
            "Optional parameter no caller passes",
            "No call passes '{0}' to '{1}': the parameter is a constant");

        public static readonly DiagnosticDescriptor OptionalParameterAlwaysPassed = Rule("GEL029", Consistency,
            "Optional parameter every caller passes",
            "Every call passes '{0}' to '{1}': the default is dead weight");

        public static readonly DiagnosticDescriptor PreferenceKeyOneSided = Rule("GEL030", DeadCode,
            "Preference key is only read or only written",
            "PreferenceConstants.{0} is {1}");

        public static readonly DiagnosticDescriptor PreferenceKeyLiteral = Rule("GEL031", Consistency,
            "Preference key written as a string literal",
            "Preference keys live in PreferenceConstants; a literal here hides a key from the one-sided check");

        public static readonly DiagnosticDescriptor RdXmlTypeMissing = Rule("GEL032", DeadCode,
            "rd.xml names a type that no longer exists",
            "'{0}' in Default.rd.xml is not a type in this app; .NET Native reports it only as ILT0027");

        public static readonly DiagnosticDescriptor OrphanedDocComment = Rule("GEL033", DeadCode,
            "Documentation comment documents nothing",
            "This /// comment is followed by no declaration");

        public static readonly DiagnosticDescriptor PageViewModelOutsideBase = Rule("GEL034", Consistency,
            "Page holds its view model outside BasePage",
            "Pages declare ViewModelType and read base.ViewModel; a view model held elsewhere is skipped by the base's lifecycle");

        public static readonly DiagnosticDescriptor ShadowState = Rule("GEL035", Consistency,
            "Same-named mutable state in several classes",
            "'{0}' is reassigned after construction in {1}: the shape of a hand-synced copy of state with one real owner",
            "Same name is not the same thing: list a name that was read and found not to be a copy in gelatinarm_shadow_state_known in .editorconfig, with the reason beside it.");

        public static readonly DiagnosticDescriptor RepeatedBlock = Rule("GEL036", Consistency,
            "Repeated block of statements",
            "These {0} statements also appear at {1}: write the block once");

        public static readonly DiagnosticDescriptor AsyncLambdaToVoidDelegate = Rule("GEL037", Safety,
            "Async lambda converted to a void-returning delegate",
            "This async lambda becomes {0}: it runs as async void, so the caller's await ends at its first await and a later exception crashes the app",
            "Pass a Func<Task> (UiHelper and BaseViewModel have that overload) or make the delegate type Task-returning. An event subscription (+=) is the one place async void belongs.");

        public static readonly DiagnosticDescriptor BlankLineBeforeClosingBrace = Rule("GEL038", Consistency,
            "Blank line before a closing brace",
            "The line before this closing brace is blank");

        public static readonly DiagnosticDescriptor ConditionalAfterDereference = Rule("GEL047", Consistency,
            "Null-conditional access after an unconditional dereference",
            "'{0}' was dereferenced above in this block, so it is not null here: the '?.' says otherwise",
            "A member access that would have thrown on null has already run on the same expression, with no assignment to it in between; the later null-conditional access reads as doubt the code has settled.");

        public static readonly DiagnosticDescriptor EnumMemberNeverProduced = Rule("GEL048", DeadCode,
            "Enum member is compared or switched on but never produced",
            "'{0}.{1}' is only matched (switch arms, comparisons); nothing in the code produces it, so those arms are dead",
            "A value nothing assigns, passes or returns cannot reach the arms that match it. A member named in XAML counts as produced.");

        public static readonly DiagnosticDescriptor AdjacentIfsOneBody = Rule("GEL050", Consistency,
            "Adjacent if statements with the same body",
            "This if does what the one before it does: one if with the conditions joined by || says it once",
            "Two if statements in a row, neither with an else, whose bodies are token-for-token the same; SonarAnalyzer's S1871 stops at else-if chains and switch sections.");

        public static readonly DiagnosticDescriptor MarkupPrefixUndeclared = Rule("GEL051", Xaml,
            "Prefix in a markup extension that no xmlns declares",
            "'{0}:' in {1} is declared by no xmlns on this page; the XAML compiler never reads it (an ignorable or design-time value), so it names nothing",
            "An undeclared prefix inside a design-time value (d:DataContext) survives the build because mc:Ignorable hides it; it still points nowhere.");

        public static readonly DiagnosticDescriptor TaskDiscardedWithFireAndForget = Rule("GEL049", Safety,
            "Task discarded where FireAndForget is at hand",
            "'_ = ...' drops this Task: a failure in it is never observed, while FireAndForget (the type's own, or AsyncHelper's) would log it",
            "BaseViewModel and BaseService give every derived type FireAndForget, and AsyncHelper.FireAndForget takes a logger; both await the task and report an exception. A Task.Run whose lambda catches its own exceptions is left alone.");

        public static readonly DiagnosticDescriptor UsingOutOfOrder = Rule("GEL053", Consistency,
            "Using directive out of the repo's order",
            "'{0}' belongs before '{1}': System first, then Windows, then the rest, each alphabetical (aliases last)",
            "Every file lists its usings the same way; IDE0055 does not order them.");

        public static readonly DiagnosticDescriptor StyleSpelledOut = Rule("GEL054", Xaml,
            "Attributes that a Style in scope already holds",
            "This {0} spells out every setter of the Style '{1}' ({2}); Style=\"{{StaticResource {1}}}\" says the same",
            "A keyed Style for the element's type, in the page's resources or App.xaml, whose setters (all of them, literal values) the element repeats attribute by attribute. Styles with BasedOn or a Template are not matched.");

        public static readonly DiagnosticDescriptor DesignTimeAttribute = Rule("GEL055", Xaml,
            "Design-time attribute",
            "{0} is read by the designer alone, which this project never opens; the XAML compiler skips it, so it and the xmlns it needs are dead",
            "d:DesignHeight, d:DesignWidth, d:DataContext and mc:Ignorable exist for Blend and the Visual Studio designer. The console never sees them and the designer is no part of the workflow (Docs/DEV_SETUP.md).");

        public static readonly DiagnosticDescriptor StyleDuplicated = Rule("GEL056", Xaml,
            "Two keyed Styles with the same setters",
            "Style '{0}' repeats '{1}' setter for setter, for the same TargetType: one key would do",
            "Two names for one look drift apart; the elements naming one should name the other.");

        public static readonly DiagnosticDescriptor ButtonContentCentred = Rule("GEL057", Xaml,
            "Button content centring itself",
            "{0}=\"Center\" on this panel repeats what the Button does with its content: the presenter is already centred and sized to its content",
            "Control.HorizontalContentAlignment and VerticalContentAlignment default to Center (Microsoft's reference), and the Button's default style leaves them so; a panel inside a Button is placed by the presenter, so its own Center alignment changes nothing unless the Button's Style moves the content elsewhere.");

        public static readonly DiagnosticDescriptor StyleWideAttribute = Rule("GEL058", Xaml,
            "Attribute every user of a Style sets alike",
            "All {0} elements with Style '{1}' set {2}=\"{3}\": the Style holds it once",
            "Three or more elements naming one keyed Style that each carry the same attribute and value; event handlers, bindings, names and Grid positions are left out.");

        public static readonly DiagnosticDescriptor CatchLogsByHand = Rule("GEL059", Consistency,
            "Catch that logs by hand where ErrorHandler is at hand",
            "This catch logs with Logger.{0}: ErrorHandler.HandleError(ex, CreateErrorContext(...)) is what the type's other catches do, and it logs at the context's severity",
            "A `catch (Exception)` whose first statement is a LogWarning or LogError, in a type that has ErrorHandler (a view model, service, page or control). A catch at Debug is a chosen quiet path (a per-second tick, an expected miss) and is not reported.");

        public static readonly DiagnosticDescriptor LogThenThrow = Rule("GEL060", Consistency,
            "Log call right before a throw",
            "{0} right before the throw reports what the exception carries; the catch that handles it logs it, so this line is the same failure twice",
            "The message belongs in the exception. Where the log said more than the exception's message, the message says it now.");

        public static readonly DiagnosticDescriptor StyleOverridden = Rule("GEL061", Xaml,
            "Style whose every setter the element sets again",
            "This {0} names Style '{1}' and then sets {2} itself: the Style contributes nothing here",
            "A keyed Style (page or App.xaml, no BasedOn or Template) each of whose setter properties the element also carries as an attribute. Either the element's attributes are the intended look and the Style reference is dead, or they repeat the Style and belong in it.");

        public static readonly DiagnosticDescriptor LoneTabIndex = Rule("GEL062", Xaml,
            "TabIndex on the only element of its page that has one",
            "TabIndex orders elements among each other; as the only one on the page it orders nothing",
            "Tab order is relative: one TabIndex in a file has no other to come before or after.");

        public static readonly DiagnosticDescriptor AttachedOutsidePanel = Rule("GEL063", Xaml,
            "Panel attached property on an element no panel arranges",
            "{0} is read by the {1} that arranges the element; this element's parent is a {2}, so nothing reads it",
            "Grid.Row, Grid.Column and their spans mean something on a Grid's child; Canvas.ZIndex, Left and Top on any Panel's child. A UserControl, Border, ScrollViewer or Button holds one content element and arranges it itself.");

        public static readonly DiagnosticDescriptor RethrowOnlyCatch = Rule("GEL064", Consistency,
            "Catch that only rethrows",
            "This catch takes {0} only to throw it on unchanged: a filter on the catch that follows, when (!(ex is {0})), leaves it uncaught",
            "A catch whose whole body is `throw;` exists to keep one exception type away from the general catch after it. An exception filter says that on the general catch itself, and the exception never unwinds into a handler.");

        public static readonly DiagnosticDescriptor PropertyElementAsResource = Rule("GEL065", Xaml,
            "Property element holding one StaticResource",
            "<{0}> holds only a StaticResource '{1}': {2}=\"{{StaticResource {1}}}\" says the same in one line",
            "A <StaticResource ResourceKey=\"K\" /> as a property element's only child is the markup extension {StaticResource K} written as an element; the pages use the attribute form elsewhere.");

        public static readonly DiagnosticDescriptor StretchWithFixedSize = Rule("GEL066", Xaml,
            "Stretch alignment beside a fixed size",
            "{0}=\"Stretch\" has nothing to stretch: {1}=\"{2}\" fixes the size, so the element is placed at the default alignment instead",
            "With Width set, HorizontalAlignment=Stretch behaves as the layout's default (Left for a Button, Center for a Grid child); the same for Height and VerticalAlignment. The attribute says nothing.");

        public static readonly DiagnosticDescriptor AttributeRepeatsStyle = Rule("GEL067", Xaml,
            "Attribute repeating what the element's Style sets",
            "{0}=\"{1}\" is what Style '{2}' already sets on this {3}",
            "The element names a Style (page or App.xaml, BasedOn followed) whose setter gives the same value. Foreground=\"White\" counts as SystemControlForegroundBaseHighBrush: on the console's dark theme they are one colour, and on a light theme the brush is the right one.");

        public static readonly DiagnosticDescriptor BindModeConvention = Rule("GEL068", Xaml,
            "Binding mode written against the convention",
            "{0}",
            "Every x:Bind states its own Mode: OneWay where the value changes, nothing (OneTime, the default) where it does not. A page-level x:DefaultBindMode changes what a bare x:Bind means, and Mode=OneTime written out where OneTime is already the default says nothing.");

        public static readonly DiagnosticDescriptor PageBackground = Rule("GEL069", Xaml,
            "Page background other than the theme brush",
            "This page's Background is {0}; every page uses {{StaticResource ApplicationPageBackgroundThemeBrush}}, the player VideoBackgroundBrush",
            "One background for every page, the theme's own. A page that paints its own colour differs from the others on purpose or by accident; the owner chose the rule. The player is black on purpose: the bars around video that does not fill the screen are its background (the owner, from the console).");

        public static readonly DiagnosticDescriptor TextDimmedByHand = Rule("GEL070", Xaml,
            "Text dimmed by hand",
            "{0}=\"{1}\" dims this text by hand: dimmed text is the BaseMedium brush (StandardCaptionTextStyle at 12, SmallCaptionTextStyle at 11, or Foreground=\"{{StaticResource SystemControlForegroundBaseMediumBrush}}\" at another size)",
            "Secondary text was dimmed five ways: Opacity, a translucent white, LightGray, Gray, a grey hex. The owner chose one: the theme's BaseMedium foreground (60% white on the console), which the caption styles carry. An Opacity bound to data (a watched item) is not dimming by hand.");

        public static readonly DiagnosticDescriptor ColourLiteral = Rule("GEL071", Xaml,
            "Colour written as a literal",
            "{0}=\"{1}\": a colour belongs in the palette (App.xaml) or the theme, named once; the scrims are OverlayScrimBrush and TintScrimBrush, the tints the SystemControlBackground* brushes, error text SystemControlErrorTextForegroundBrush",
            "Seven black scrims of different opacities and four white tints were written out where used, and an error line was Red beside the theme's error brush. A hex or named colour is allowed only where a keyed Color or SolidColorBrush defines it.");

        public static readonly DiagnosticDescriptor ProgressBarByHand = Rule("GEL072", Xaml,
            "Progress bar drawn by hand",
            "{0}: a progress bar is two Rectangles, Height=\"{{StaticResource ProgressBarHeight}}\", the track filled with SystemControlBackgroundBaseMediumLowBrush and the value with SystemControlHighlightAccentBrush, no Opacity",
            "Progress bars were 2, 4 or 6 high with tracks of four colours. One height and one pair of brushes, from the palette.");

        public static readonly DiagnosticDescriptor StructureComment = Rule("GEL073", Consistency,
            "Region or banner comment organising a file",
            "{0} organises the file by hand: the files are organised by members and types, not by regions or banners",
            "Sixteen files used #region under ad hoc names and one model used // === banners; the other 130 did not. A region hides code in the editor and says nothing to the reader of the file.");

        public static readonly DiagnosticDescriptor CancellationLogged = Rule("GEL074", Consistency,
            "Cancellation logged",
            "This catch of OperationCanceledException only logs: a cancellation is not an error and is not logged; a why comment says what superseded the work",
            "ErrorHandler already treats a cancellation as Debug-level noise. Four catches of it said so in a comment, three logged it. A catch that also does work (shows a timeout, restores state) is not reported.");

        public static readonly DiagnosticDescriptor NullableBoolCoalesced = Rule("GEL075", Consistency,
            "Nullable bool coalesced",
            "'{0}' is a nullable bool: '{0} == true' says it; '?? false' is the same test spelled another way",
            "Thirty sites test a nullable bool with == true, sixteen with ?? false. One spelling.");

        public static readonly DiagnosticDescriptor AsyncLambdaOnlyAwaits = Rule("GEL076", Consistency,
            "Async lambda that only awaits one call",
            "async () => await X(...) is () => X(...): the task is handed on as it is, without a state machine around it",
            "A lambda whose whole body is an await of a Task-returning call hands the same task on when written without async and await.");

        public static readonly DiagnosticDescriptor GlyphButtonContent = Rule("GEL077", Xaml,
            "Glyph written as a Button's Content",
            "This Button shows a glyph through its Content and FontFamily: every other icon button holds a <FontIcon Glyph=\"...\" />",
            "One button set FontFamily=\"Segoe MDL2 Assets\" and a glyph as Content; the others hold a FontIcon, whose FontFamily is that by default.");

        public static readonly DiagnosticDescriptor ColourAsBrush = Rule("GEL078", Xaml,
            "Colour resource where a brush is expected",
            "{0}=\"{1}\": a Color stands in for a Brush here; the palette's brush ({2}) is the one to name",
            "SystemAccentColor as a Background or Fill is converted to a brush at load; SystemControlHighlightAccentBrush is that brush, named.");

        public static readonly DiagnosticDescriptor AssemblyVersionDiffers = Rule("GEL079", Consistency,
            "Assembly version differs from the package version",
            "{0} says {1} where Package.appxmanifest says {2}: the two versions move together",
            "The Store reads the manifest's version, the assembly's own says what it is; a bump that reaches one and not the other leaves them telling different stories.");

        public static readonly DiagnosticDescriptor LoadingOverlaySpelledOut = Rule("GEL080", Xaml,
            "Loading overlay built by hand",
            "A scrim with a bound Visibility over a ProgressRing is the LoadingOverlay control spelled out: <controls:LoadingOverlay IsLoading=\"...\" LoadingText=\"...\" />",
            "Every page shows its busy state through LoadingOverlay, which also holds focus while it shows; one page drew its own.");

        public static readonly DiagnosticDescriptor IconLabelButton = Rule("GEL081", Xaml,
            "Icon and label in a Button spaced by hand",
            "{0}: an icon and a label in a Button sit in a horizontal StackPanel with Spacing=\"8\", the icon carrying no Margin or alignment, the label no Margin, Style or size of its own (the Button's font and brush are its)",
            "The gap between an icon and its label was a 4px or 8px Margin on the label, a Margin on the icon, or Spacing of 4 or 8 on the panel, and the label was styled or not. One shape.");

        public static readonly DiagnosticDescriptor FillDimmedByHand = Rule("GEL082", Xaml,
            "Solid fill dimmed with Opacity",
            "{0}=\"{1}\" with Opacity=\"{2}\": the palette's BaseLow (dividers) or BaseMediumLow (tracks) brush is that tint, named",
            "A divider was white at 0.3 opacity while the next one was the BaseLow brush; text has the same rule in GEL070.");

        public static readonly DiagnosticDescriptor MarginInsideSpacing = Rule("GEL083", Xaml,
            "Margins inside a StackPanel that has Spacing",
            "{0} children of this StackPanel add a Margin along its axis to its Spacing of {1}: one mechanism, Spacing, sets the gaps",
            "Spacing on the panel and a Margin on each child (or on their Style) both set the same gap, and the sum was not what either said; a single nudged child is left alone.");

        public static readonly DiagnosticDescriptor CollectionReshaped = Rule("GEL084", Consistency,
            "Collection built in one shape and converted at once",
            "{0} returns {1} and every call turns it into {2}: build the {2} in the first place",
            "A method returned an array, its callers called ToList() on it, and the array existed only to be copied.");

        public static readonly DiagnosticDescriptor AsyncMethodOnlyAwaits = Rule("GEL086", Consistency,
            "Async method that only awaits one call",
            "{0}'s whole body is one awaited call: return the call's task as it is (no async, no await, no ConfigureAwait), as the other one-call methods do",
            "Eleven methods wrapped a single Task-returning call in async and await, a state machine around a task that could be handed on; GEL076 says the same of lambdas. A `return await` that converts the task's type (Task<List<T>> to Task<IEnumerable<T>>) is not this.");

        public static readonly DiagnosticDescriptor SizeAgainstBounds = Rule("GEL087", Xaml,
            "Size written against its own bounds",
            "{0}",
            "A flyout was MinWidth and MaxWidth 800 (that is Width), and a combo box a Width of 120 under a Style's MinWidth of 150 (the minimum wins, the Width says nothing).");

        public static readonly DiagnosticDescriptor SpacingByMargin = Rule("GEL088", Xaml,
            "StackPanel spaced by its children's Margins",
            "Every child after the first opens with a Margin of {0} on the panel's axis and nothing else: Spacing=\"{0}\" on the panel says it once",
            "A row of buttons carried Margin=\"8,0,0,0\" on all but the first where the rows beside it used Spacing; GEL083 is the other half (Margins on top of Spacing).");

        public static readonly DiagnosticDescriptor SharedStyleOverride = Rule("GEL089", Xaml,
            "The same attribute over one Style across the app",
            "{0} elements in {1} files name {2} and set {3}=\"{4}\": a Style based on {2} in App.xaml holds that once",
            "Dimmed body text was StandardBodyTextStyle plus the BaseMedium brush on twenty-one elements in eleven files; GEL042 sees one file at a time. TextWrapping and TextTrimming are left out: how one text lays out, not a look.");

        public static readonly DiagnosticDescriptor LiteralRepeatsArray = Rule("GEL085", Consistency,
            "String literal repeats a static array's items",
            "\"{0}\" lists the items of {1}: string.Join(\"{2}\", {1}) keeps them in one place",
            "The codec names were an array for one check and a comma-joined literal for another; a codec added to one was missed by the other.");

        public static readonly DiagnosticDescriptor ConsecutiveLogCalls = Rule("GEL052", Consistency,
            "Consecutive log calls at one level",
            "{0} follows another {0} with nothing between them: one event, one line (join the messages)",
            "Two log calls in a row at the same level describe one event in two lines; the crash handler in App.xaml.cs, which dumps an exception field by field, is exempt.");

        public static readonly DiagnosticDescriptor AnyThroughConditionalOnCounted = Rule("GEL039", Consistency,
            "Any() through a conditional access on a collection that has a Count",
            "'{0}' has a {1}: write 'x is {{ {1}: > 0 }}' (Roslynator's RCS1080 stops at the conditional access)");

        public static readonly DiagnosticDescriptor HandBuiltCommand = Rule("GEL043", Consistency,
            "Command built by hand in an observable object",
            "'{0}' is constructed here: [RelayCommand] on the method generates it, as the other view models do",
            "A command wrapping a virtual method is left alone: the attribute cannot dispatch to overrides.");

        public static readonly DiagnosticDescriptor HandWrittenObservableProperty = Rule("GEL044", Consistency,
            "Observable property written by hand",
            "'{0}' only reads its field and calls SetProperty: [ObservableProperty] on the field generates it, as the other view models do");

        public static readonly DiagnosticDescriptor HandWrittenFactory = Rule("GEL045", Consistency,
            "Service factory does what the container does",
            "This factory only resolves services and calls a constructor: register the type ({0}) and let the container build it");

        public static readonly DiagnosticDescriptor TypeTestSubsumed = Rule("GEL041", Consistency,
            "Type test made redundant by a test for its base type",
            "'{0}' derives from '{1}', which is tested beside it: the narrower test adds nothing");
    }
}
