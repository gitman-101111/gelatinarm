# Conventions

How code is written here. Structure: [ARCHITECTURE.md](ARCHITECTURE.md). Build
and test: [DEV_SETUP.md](DEV_SETUP.md).

## Scope of a change

- A user-facing control does what it says: wire it fully or remove it.
- Code nothing reaches is removed: settings with no UI, commands nothing binds,
  virtual hooks nothing overrides or calls. In a single-assembly app a public
  member with no reference is dead.
- Say what was verified: "compiles" and "tested on a console" are different claims.

## Commits

- One concern per commit. Never mix a formatting sweep into a behaviour change.
- Subject prefix: `feat:`, `fix:`, `refactor:`, `perf:`, `docs:`, `build:`,
  `chore:`. The *why* goes in the body.
- Incoming contributions are cherry-picked with their authorship.

## Language and project

- C# 9 (`LangVersion` 9.0): no newer syntax.
- Every `.cs` file is listed in `Gelatinarm.csproj` (`<Compile Include>`); the
  project does not glob. Every folder with XAML is in the `AdditionalFiles`
  line, or the XAML rules skip its pages.
- One type per file, named after the file, with two exceptions: a service
  interface sits above its implementation (`Foo.cs` holds `IFoo`, then `Foo`),
  and a class split by concern is partial across files named for the concern
  (`MediaPlayerViewModel.Hls.cs`).
- A file lives in its feature's folder (`Library/`, `Player/`), or under
  `Shared/` when more than one feature uses it; the namespace follows the folder.
- Tuned numbers (delays, limits, retry counts, skip lengths) go in the folder's
  constants class (`PlayerConstants`, `LibraryConstants`), named for what they mean.
- Derive from the base class for the kind of thing (`BasePage`, `DetailsPage`,
  `BaseViewModel`, `BaseService`, `BaseControl`) and use its helpers:
  `CreateErrorContext`, `FireAndForget`, `RunOnUIThreadAsync`, `GetService<T>`,
  `RetryAsync` (services), `TryGetGuidFromParameter`/`TryGetUserIdGuid`,
  `StopMusicBeforeUserChangeAsync`.
- Only the root of a hierarchy is `Base...`; a middle class is named for what it
  is (`DetailsViewModel`; `SettingsViewModelBase` is the exception).

## Error handling

In a `catch`, use `ErrorHandler.HandleErrorAsync` (or `HandleError` where you
cannot await) with an `ErrorContext` from `CreateErrorContext`, not a bare
`Logger.LogError`. It logs cancellation (`TaskCanceledException`,
`OperationCanceledException`) at Debug and HTTP 502/503/504 as a Warning, always
in structured form, and maps exceptions to messages a user can act on (bad
credentials, permissions, rate limiting, server restarting, DNS versus
certificate failures).

Pass `showUserMessage: true` only where the user can act on it. A service that
must return something uses the generic overload with a default.

Exceptions to the rule:

- `throw;` after cleanup, where the caller handles the failure. Logging and
  rethrowing reports one failure twice.
- Typed catches for expected control flow, such as `catch
  (OperationCanceledException)` at Debug.
- `ErrorHandlingService` itself (deriving from `BaseService` would be circular).
- The unhandled-exception handler in `App.xaml.cs`, which must stay silent.

An empty `catch` carries a comment saying why.

## Logging

Constant templates with named placeholders, never interpolated strings (CA2254):

```csharp
Logger.LogInformation("Playing {ItemName} from {Position}", item.Name, position);
```

Never log a credential: stream URLs go through `UrlHelper.RedactApiKey`; tokens,
passwords and the Quick Connect secret are not logged.

## Async and threading

- `async void` only for event handlers, with the body in `try`/`catch` and the
  error handler: an escaping exception terminates the app.
- Unawaited work goes through `FireAndForget`, which logs failures. The one
  exception to that is `UiHelper.RunOnUIThreadAsync(Action)`, which never throws.
- A method with nothing to await is synchronous (overrides of an async-capable
  virtual aside).
- UI updates from another thread go through `RunOnUIThreadAsync` (view models)
  or `UiHelper.RunOnUIThreadAsync`, not `Dispatcher.RunAsync`.
- In services, `ConfigureAwait(false)` where the continuation does not touch UI
  state; UWP has real UI-thread affinity.

## Navigation, dialogs, user data

- `NavigationService` for navigation, `NavigateToItemDetails` for media items.
  Never `Frame.Navigate`.
- `IDialogService` (`ShowMessageAsync`, `ShowConfirmationAsync`, both
  `(title, message)`) for dialogs, never a `ContentDialog` or `MessageDialog`;
  the crash handler in `App.xaml.cs` is the exception. No exception text in a
  message.
- Favourite and watched state go through `IUserDataService`.

## XAML

- `x:Bind` defaults to `OneTime`. A property that changes needs `Mode=OneWay`,
  unless the page root sets `x:DefaultBindMode="OneWay"` (Album, Artist and
  Person details). Check the root before changing either style.
- A `DataTemplate` with `x:Bind` needs `x:DataType` and cannot bind to the
  page's view model; use an event handler or `{Binding}` with `ElementName`.
- Resource scope: a page's resources are visible only to that page;
  `Details/DetailsResources.xaml` to the pages that merge it; `App.xaml` (with
  `Styles/`) to everyone. Check the scope before calling a key unused or
  duplicated.
- Keys like `ButtonBackground` or `AccentButtonStyle` are WinUI theme keys: the
  default templates look them up implicitly, so they show no `{StaticResource}`
  reference and are not dead.
- `x:Name` only where code or a binding refers to it. Names are also looked up
  as strings (`FindName("...")`): search for the string before removing one.
- The form is XAML Styler's default. The build checks every file;
  `msbuild Gelatinarm.csproj /t:FormatXaml` restores it. An attribute at the
  element's default, a wrapper with one child and nothing of its own, a
  `{Binding}` beside `x:Bind` or an `x:Name` nothing addresses is a build
  warning (DEV_SETUP.md, the project's own analyzers).
- Set `XYFocusUp`/`Down`/`Left`/`Right` where the geometric default sends focus
  somewhere wrong or off-screen; test every screen with a controller only.
- A `Slider` or `ComboBox` bound to a setting uses the bounds `AppPreferences`
  clamps to (`UiConstants`).

## Comments

Comments say *why*: a constraint, a platform quirk, a decision someone would
otherwise undo. No narration of the next line, no headers over empty sections,
no "removed X" notes.
