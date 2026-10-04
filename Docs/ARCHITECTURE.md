# Architecture

Gelatinarm is a UWP (C#/XAML) Jellyfin client for Xbox. Build and test:
[DEV_SETUP.md](DEV_SETUP.md). Code rules: [CONVENTIONS.md](CONVENTIONS.md).

## Layout

| Folder | Holds |
| --- | --- |
| `SignIn/`, `Home/`, `Library/`, `Search/`, `Favorites/`, `Details/`, `Music/`, `Settings/` | One feature each: its pages, view models, services, models, converters and constants, in one namespace (`Gelatinarm.Library`). Pages derive from `BasePage`; the detail pages (movie, season, collection, person, album, artist) from `DetailsPage`; view models from `BaseViewModel` (`DetailsViewModel<T>`, `SettingsViewModelBase`); services from `BaseService`, each interface above its implementation in the same file. |
| `Player/` | The video player: `MediaPlayerPage`, `MediaPlayerViewModel` (partial by concern: `.Hls.cs`, `.Stats.cs`, `.SkipSegments.cs`, `.Input.cs`), `PlaybackControlService`, the resume, state, buffering and seek coordinators, `MediaQueueService`, `ControllerInputService`, the player's models and converters. |
| `Playback/` | Shared by both players: `MediaPlaybackService` (session reports), `DeviceProfileService`, `MediaOptimizationService`, `AudioTrack`, `SubtitleTrack`. |
| `Shared/` | Used by more than one feature: `Base/` (the base classes), `Navigation/` (`NavigationService`, `RootContainer`, the app shell), `Errors/`, `Preferences/`, `Async/`, `Ui/` (`UiHelper`, `ControllerInputHelper`, `LoadingOverlay`, `UnwatchedIndicator`, the app-wide converters), `Images/`, `Device/`, `Server/`. |
| `Styles/` | `App.xaml`'s resources as merged dictionaries, in merge order: `Colours.xaml`, `Text.xaml`, `Controls.xaml`, `Templates.xaml`. `App.xaml` holds the converter instances. |
| `Details/DetailsResources.xaml` | A resource dictionary merged by the six detail pages. |

Tunable numbers live in each folder's constants class (`PlayerConstants`,
`Shared/Async/RetryConstants`).

## Layers

```
Page (XAML + code-behind, BasePage)
  │  x:Bind / Binding, commands, event handlers
ViewModel (BaseViewModel)
  │  constructor-injected service interfaces
Services (BaseService)
  │
Jellyfin SDK (JellyfinApiClient)    Windows.Media.Playback.MediaPlayer
```

View models never call the API; services do. Pages talk to their own view model
and to `NavigationService`.

## Startup

1. `App` constructor: `ConfigureServices` builds the DI container
   (`Microsoft.Extensions.DependencyInjection`). Registration only records
   descriptors; the `try`/`catch` in the factory lambdas run at first resolution.
2. `OnLaunched` creates the `RootContainer` shell (the navigation frame and the
   `MusicPlayer` bar) and picks the first page: no server URL or stored token,
   `ServerSelectionPage`; two or more saved profiles, `ProfileSelectionPage`;
   otherwise `MainPage`, with the token validated in the background (a rejected
   token returns to server selection).

## Dependency injection and lifetimes

All registrations are in `App.ConfigureServices`.

- Services are singletons, except `ControllerInputService` (transient, one per
  player page). One instance under several interfaces: `MediaQueueService` as
  `IEpisodeQueueService` and `IMediaNavigationService`; `MediaPlaybackService`
  as `IMediaSessionService`. Music has its own queue (`MusicQueueService`,
  `IPlaybackQueueService`) and player (`MediaControlService`).
- View models are transient, except `MainViewModel` and `LibrarySelectionViewModel`.
- Singletons outlive a user change. Every user or server change goes through
  `AuthenticationService`, which clears the API cache and raises `UserChanged`;
  per-user state in a singleton subscribes to it (`MainViewModel` empties the
  home screen, `LibrarySelectionViewModel` remembers its list's user). The
  signed-in user's id is read from the session at the point of use.
- Constructor injection, except where the container cannot construct the object
  (converters, some helpers): `ServiceLocator`, wrapped by the base classes as
  `GetService<T>()`.

## Pages

`BasePage` runs the lifecycle; pages override its hooks:

- New navigation: `InitializePageAsync`; Back: `OnNavigatedBackAsync`; both
  followed by `RefreshDataAsync`.
- `OnNavigatedFrom`: `CancelOngoingOperations`, then `CleanupResources`.
- A page with `ViewModelType` gets its view model resolved and assigned. An
  `IPageViewModel` (the sign-in pages) is initialized from the navigation
  parameter by the default `InitializePageAsync` and disposed by the default
  `CleanupResources`.
- Controller navigation is configured in the constructor
  (`ControllerInputHelper.ConfigurePageForController`). First focus goes to
  `InitialFocusControl` (read on load), else the first focusable control, unless
  the page has already focused one of its own controls. `HandleBackNavigation`
  intercepts the system Back request.
- A cached page loads and unloads on every return: handlers dropped on leaving
  are re-added on arrival.

`DetailsPage` adds `InitializeViewModelAsync(parameter)` on every visit (`null`
on Back; `ResolveNavigationParameter` recovers it) and `MoveToContentArea`, which
focuses Play or Resume once content has loaded. `MusicDetailsPage` (album,
artist) adds the track rows' context menus.

Page caching, declared per page:

| Cached (`Enabled`/`Required`) | Not cached |
| --- | --- |
| Library, Search, Season, Artist, Collection | Movie, Album, Person, the rest |

A cached page keeps its view model, which reloads when what it shows has changed.

## Navigation

All navigation goes through `NavigationService`. `NavigateToItemDetails` picks
the page by item type:

| Item type | Page |
| --- | --- |
| Movie | `MovieDetailsPage` |
| Series, Season, Episode | `SeasonDetailsPage` (an episode opens in its season) |
| MusicAlbum | `AlbumDetailsPage` |
| MusicArtist | `ArtistDetailsPage` |
| Person | `PersonDetailsPage` |
| BoxSet | `CollectionDetailsPage` |
| Audio | none: it plays in the `MusicPlayer` bar |

The back stack is trimmed above `UiConstants.MaxBackStackDepth`; the player page
leaves it when playback is left, and consecutive player pages do not pile up.

## Sign-in and profiles

Username and password, or Quick Connect, through the Jellyfin SDK. Access tokens
are stored in the Windows `PasswordVault`, keyed by user id, never in
preferences. Each user who signs in on the device is saved as a `UserProfile`
for switching without signing in again (`ProfileSelectionPage`). Before a user
change, `StopMusicBeforeUserChangeAsync` stops music (waiting briefly for the
stop report) and the home screen cache is cleared.

## Preferences

Two stores:

- Per-user `AppPreferences`, a JSON blob keyed by user
  (`GetAppPreferencesAsync`/`UpdateAppPreferencesAsync`): the Settings page.
  Settings save as they change (`SettingsViewModelBase.UpdateAppPreferenceAsync`);
  `AppPreferences` clamps each value to its range on write.
- Device-level keys (`GetValue`/`SetValue` with `PreferenceConstants`) for what
  applies before sign-in: the server URL and the "allow untrusted certificates"
  switch, which the HTTP handler reads.

## Playback

Video plays on `MediaPlayerPage`; music in the `MusicPlayer` bar, which lives in
the shell and survives navigation.

| Piece | Responsibility |
| --- | --- |
| `MediaPlayerViewModel` | A video session: start, player state, progress reports, next episode, intro/outro skip, the stats overlay (Y). |
| `MediaPlaybackService` | `GetPlaybackInfoAsync` (how to play an item) and the start, progress and stop reports, for both players. |
| `PlaybackControlService` | Stream selection (`PlaybackSourceResolver`), resume (`PlaybackResumeCoordinator`), audio and subtitle tracks and the restart a change needs. |
| `MediaControlService` | The music player's `MediaPlayer`: play, pause, repeat, state events. |
| `MediaQueueService`, `MusicQueueService` | The video queue and next-episode lookup; the music queue, shuffle and repeat order. |
| `DeviceProfileService`, `MediaOptimizationService` | The device profile sent to the server; playback tuning. |
| `DisplayModeService` | Switches the display's mode to suit a video, and back on leaving the player (4K edition). |
| `MusicPlayerService` | The music queue, instant mix, system media transport controls, background audio. Pages and the mini player use `IPlaybackQueueService`. A track the console cannot open is streamed from the server instead: lossless sources as FLAC, lossy as MP3. Those streams have no length, so music does not seek. |

**Direct play or server stream.** `DeviceProfileService` describes the console;
the server decides. Containers and codecs follow the codec test
([CODEC_TESTING.md](CODEC_TESTING.md)). Hardware HEVC is read at runtime with
`ProtectionCapabilities.IsTypeSupported`; `NotSupported` (the original Xbox One)
removes HEVC from direct play and server streams alike. A direct play that fails
to open, or whose resume seek does not finish, is retried once as a server stream
(`MediaPlayerViewModel.TryRecoverFromMediaFailure`,
`PlaybackControlService.RetryAsServerStreamAsync`), at the resume point if
playback never started. The app renders no subtitles, so they are declared
`Encode` (burned in by the server). In a direct play the audio track is selected
in the player (`MediaPlaybackItem.AudioTracks`, by position among the file's
audio streams); a server stream carries one track, so switching reopens it. Two
server rules:

- Every direct-play profile declares `Type` (`Video` or `Audio`). An omitted
  `Type` reads as `Audio`, and video then ends in `DirectPlayError`.
- Every codec profile matching a codec applies, and all their conditions must
  pass, so range types go in the one general HEVC range-type list.

The playback request names its `MediaSourceId`; the server applies the chosen
tracks only then. Without a `TranscodingUrl`, `PlaybackSourceResolver` builds a
static stream URL with the token in it (`MediaPlayer` sends no headers);
otherwise the server's HLS stream plays. The reported play method follows, and
the stats overlay reads the server session for what was copied or re-encoded.

The `Player/` coordinators: `PlaybackStateCoordinator` snapshots player state on
the UI thread and drops duplicate events, `BufferingStateCoordinator` owns
buffering start/end and seek-aware timeouts, `SeekCompletionCoordinator` checks
that seeks landed; `PlaybackSessionState` holds their shared per-session state.

**Resume.** The position is sent as `StartTimeTicks`; if the stream still
starts at 0, the client seeks. For HLS a manifest offset is tracked (the server
rebuilds the manifest at a new position on large seeks and track changes) so
reported positions stay right. Resume is applied at `Playing`, not
`VideoFrameAvailable`. Logs: `[RESUME]` (what was sent), `[HLS-RESUME]`
(whether a client seek was needed).

**Background.** Video stops when the app is backgrounded; the app exits unless
music is playing.

## Controller input

- Pages: XY focus navigation, configured by `BasePage`. Set `XYFocus*` in XAML
  where the geometric default picks the wrong element. B is the system Back
  request.
- Media player: `ControllerInputService` maps keys to `MediaAction`s. B always
  goes back. While the transport controls are visible, keys go to the controls,
  except the triggers, Y, Up, and Left/Right within a second of a previous skip.

| Button | Action |
| --- | --- |
| A | Play/pause |
| B | Back |
| Y | Stats overlay |
| D-pad Up/Down | Show/hide the controls |
| D-pad Left / Right | Back 10 s / forward 30 s (`PlayerConstants`) |
| Left / Right trigger | Back / forward 10 min |

- Anywhere: holding the right trigger for 500 ms focuses the `MusicPlayer`
  bar's play/pause button (`RootContainer`).

## Error handling

`ErrorHandlingService` is the single path for caught exceptions: it logs at a
severity it chooses and maps exceptions to user-facing text when asked. The rule
for using it: [CONVENTIONS.md](CONVENTIONS.md#error-handling).

## Caching

- `CacheManagerService`: in-memory LRU, 100 MB cap. `MediaDiscoveryService` keeps
  the home rows there, each with its own lifetime; Continue Watching is not
  cached and reloads on every return to the home page.
- Images: a `BitmapImage` on the item's image URL (`ImageHelper` builds it with
  the image tag), fetched and cached by the platform; `ImageConverter` in XAML,
  `ImageLoadingService` in code.

Xbox One's memory ceiling makes allocation in playback paths and image volume in
long lists constraints.
