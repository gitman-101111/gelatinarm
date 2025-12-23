# Gelatinarm Project Documentation

## Overview
Gelatinarm is a Jellyfin client application built for Xbox using Universal Windows Platform (UWP). The application allows users to browse and play media content from their Jellyfin servers on Xbox consoles.

## Quick Reference
- **Architecture Pattern**: MVVM with Services
- **Target Platform**: Xbox One, Xbox Series S/X (UWP)
- **Min Memory**: 3GB (Xbox One constraint)
- **Primary Language**: C# with XAML
- **Key Dependencies**: Jellyfin SDK, Windows.Media.Playback
- **Navigation**: NavigationService (singleton)
- **Error Handling**: ErrorHandlingService (centralized)
- **Controller Input**: UnifiedDeviceService + MediaControllerService

## Table of Contents
1. [Project Structure](#project-structure)
2. [File Directory Reference](#file-directory-reference)
3. [Navigation Flow](#navigation-flow)
4. [Server Communication](#server-communication)
5. [Key Concepts](#key-concepts)
6. [Controller Architecture](#controller-architecture)
7. [Development Guide](#development-guide)

## Project Structure

The project follows a Model-View-ViewModel (MVVM) architecture pattern with the following main components:

```
/Volumes/gelatinarm/
├── App.xaml.cs                 # Application entry point
├── Views/                      # User interface pages
├── ViewModels/                 # Business logic for views
├── Services/                   # Core functionality providers
├── Models/                     # Data structures
├── Controls/                   # Reusable UI components
├── Converters/                 # Data transformation for UI
├── Helpers/                    # Utility functions
├── Constants/                  # Application-wide values
├── Extensions/                 # Type extension methods
└── Assets/                     # Images and resources
```

## Page Inheritance Hierarchy

```
Page (UWP Framework)
└── BasePage (Lifecycle, Services, Controller Support)
    ├── Individual Pages (MainPage, SearchPage, etc.)
    └── DetailsPage (Common functionality for media details)
        └── Detail Pages (MovieDetailsPage, SeasonDetailsPage, etc.)
```

### BasePage Features
- **Automatic ViewModel initialization** via `ViewModelType` property
- **Service resolution helpers**: `GetService<T>()`, `GetRequiredService<T>()`, `GetService(Type)`
- **Built-in services as properties**: Logger, NavigationService, ErrorHandlingService, PreferencesService, UserProfileService
- **Standard lifecycle management**: OnNavigatedTo/From, OnPageLoaded/Unloaded
- **Controller input support**: Automatic setup via ControllerInputHelper
- **Error handling integration**: Error handling via ErrorHandler property and CreateErrorContext method
- **Navigation helpers**: NavigateToItemDetails, GetSavedNavigationParameter, ResolveNavigationParameter

## File Directory Reference

### Root Files
- **App.xaml / App.xaml.cs** - Application startup, service registration, and global resources
- **Gelatinarm.csproj** - Project configuration and dependencies
- **Package.appxmanifest** - Application metadata and capabilities

### Views/ (User Interface Pages)
Each view represents a screen in the application:

- **MainPage** - Home screen showing Continue Watching, Latest Movies, TV Shows
- **LoginPage** - User authentication screen
- **ServerSelectionPage** - Choose or add Jellyfin server
- **QuickConnectInstructionsPage** - Guide for Quick Connect authentication
- **LibrarySelectionPage** - Choose which media library to browse
- **LibraryPage** - Browse content within a selected library
- **SearchPage** - Search across all media
- **FavoritesPage** - Display user's favorite items
- **SettingsPage** - Application configuration
- **MediaPlayerPage** - Video/audio playback screen (Next Episode, Episodes for TV queues)
- **MovieDetailsPage** - Movie information and actions
- **SeasonDetailsPage** - TV show season with episodes
- **AlbumDetailsPage** - Music album information
- **ArtistDetailsPage** - Artist profile and albums
- **PersonDetailsPage** - Actor/director profile
- **CollectionDetailsPage** - Curated collection of media
- **DetailsPage.cs** - Shared functionality for detail pages
- **DetailsPage.xaml** - Shared UI resources for detail pages
- **BasePage.cs** - Base class for all pages with lifecycle management, services, and controller support

### ViewModels/ (Business Logic)
Each ViewModel corresponds to a View and handles its logic:

- **BaseViewModel** - Common functionality for all ViewModels (includes `FireAndForget` helper for background tasks)
- **MainViewModel** - Fetches and manages home screen content
- **LoginViewModel** - Handles authentication logic
- **ServerSelectionViewModel** - Server discovery and connection
- **QuickConnectInstructionsViewModel** - Quick Connect process
- **LibrarySelectionViewModel** - Library listing and selection
- **LibraryViewModel** - Content browsing and filtering
- **SearchPage (self-contained)** - Search functionality implemented directly in the page
- **FavoritesViewModel** - Favorite items management
- **SettingsViewModel** - Settings management
- **MediaPlayerViewModel** - Playback control and state
- **MovieDetailsViewModel** - Movie data and actions (includes playback options for video/audio/subtitle tracks)
- **SeasonDetailsViewModel** - Season and episode data
- **AlbumDetailsViewModel** - Album tracks and playback
- **ArtistDetailsViewModel** - Artist information
- **PersonDetailsViewModel** - Person filmography
- **CollectionDetailsViewModel** - Collection contents
- **DetailsViewModel** - Generic view model for detail pages
- **NetworkSettingsViewModel** - Network configuration (used in SettingsPage)
- **PlaybackSettingsViewModel** - Playback preferences (used in SettingsPage)
- **ServerSettingsViewModel** - Server-specific settings (used in SettingsPage)

### Services/ (Core Functionality)
Services provide reusable functionality across the application:

#### Base Infrastructure
- **BaseService** - Abstract base class for all services with error handling
- **ServiceInterfaces** - Common service interface definitions (INavigationService, INavigationStateService, etc.)

#### Authentication & User
- **AuthenticationService** - User login/logout, token management
- **UserProfileService** - Current user information and preferences
- **UserDataService** - User data operations (favorites, watched status)

#### Media Discovery & Playback
- **MediaDiscoveryService** - Fetch recommended content, continue watching
- **MediaPlaybackService** - Control media playback and session management
- **MusicPlayerService** - Persistent playback controls
- **MediaOptimizationService** - Video/audio quality optimization
- **EpisodeQueueService** - TV episode queue management
- **MediaControlService** - Low-level MediaPlayer control

#### Navigation & State
- **NavigationService** - Page navigation, history, and state persistence (implements both INavigationService and INavigationStateService)

#### Device & System
- **UnifiedDeviceService** - Xbox hardware detection and capabilities
- **SystemMonitorService** - Unified performance, memory, and network monitoring

#### Data & Storage
- **PreferencesService** - User settings storage
- **CacheManagerService** - Memory cache management
- **FileCacheProvider** - Disk-based image caching implementation
- **ImageLoadingService** - Image loading with retry logic

#### Playback Support
- **PlaybackControlService** - Playback setup, stream selection, and restart flow
- **PlaybackSourceResolver** - Selects playback source and resolves streaming strategy
- **PlaybackResumeCoordinator** - Centralized resume strategy across stream types
- **PlaybackResumeModels** - Resume policies, retry config, and verification helpers
- **PlaybackRestartService** - Restart handling for track/subtitle changes with resume carry-over
- **PlaybackQueueService** - Playback queue management
- **SubtitleService** - Subtitle track handling
- **SkipSegmentService** - Intro/outro skip functionality
- **PlaybackStatisticsService** - Real-time playback metrics display
- **MediaNavigationService** - Next/previous navigation
- **MediaControllerService** - Xbox controller event-based input handling

#### UI Support
- **DialogService** - Message dialog display
- **SystemMediaIntegrationService** - System media controls
- **ErrorHandlingService** - Unified error handling and user notification

#### Network & Performance
- **DeviceProfileService** - Device capability profile

### Models/ (Data Structures)
- **AppPreferences** - Application settings structure
- **DecadeFilterItem** - Decade filter option
- **ErrorHandling** - Error context and categorization models
- **FilterItem** - Generic filter option
- **MediaPlaybackModels** - Playback-related structures (MediaPlaybackParams, etc.)
- **NetworkMetrics** - Network performance data
- **PlaybackInfo** - Media playback details
- **QuickConnectInstructionsParameters** - Quick Connect parameters
- **SearchPageParams** - Search page parameters

### Controls/ (Reusable UI Components)
- **BaseControl** - Base class for all controls with error handling
- **ControllerInputHelper** - Xbox controller support
- **LoadingOverlay** - Loading indicator
- **MusicPlayer** - Compact media player for audio playback
- **PlaybackSettings** - Playback configuration UI
- **RootContainer** - Application shell with navigation
- **UnwatchedIndicator** - New content badge

### Converters/ (Data Transformation)
- **Data/** - InverseBooleanConverter, PercentageToWidthConverter, PlayedToOpacityConverter
- **Image/** - ImageConverter, LibraryIconConverter, PersonImageConverter
- **Text/** - MediaTextConverters, RuntimeTicksConverter, SortFilterConverters, YearDisplayConverter
- **Visibility/** - VisibilityConverters
- **MediaPlayerConverters** - Media player specific conversions

### Helpers/ (Utility Functions)
- **AsyncHelper** - Async helper used by base-class `FireAndForget` wrappers
- **ImageHelper** - Image processing utilities
- **MediaPlaybackHelper** - Media playback utility functions
- **NetworkHelper** - Network-related utilities
- **ObservableCollectionExtensions** - Collection extension methods
- **RetryHelper** - Network retry logic
- **TimeFormattingHelper** - Time formatting utilities
- **UIHelper** - UI helper functions
- **ServiceLocator** - Shared service resolution for non-injected classes
- **PlaybackStateOrchestrator** - Unified playback state transitions for the UI
- **BufferingStateCoordinator** - Buffering lifecycle and timeout logic
- **ResumeFlowCoordinator** - Resume acceptance and completion reporting
- **ResumeRetryCoordinator** - Retry scheduling and tolerance evaluation
- **SeekCompletionCoordinator** - Seek completion logging and validation

### Constants/ (Application Values)
- **BrandingConstants** - Application identity
- **LibraryConstants** - Library-related constants
- **MediaConstants** - Media settings
- **MediaPlayerConstants** - Media player constants
- **PreferenceConstants** - Setting keys
- **RetryConstants** - Retry policies
- **SystemConstants** - System-level constants
- **UIConstants** - UI-related constants

## Navigation Flow

The application follows this navigation hierarchy:

```
App Launch
├─→ ServerSelectionPage (if no server configured)
│   └─→ LoginPage
│       ├─→ QuickConnectInstructionsPage (optional)
│       └─→ MainPage
└─→ MainPage (if already authenticated)
    ├─→ LibrarySelectionPage → LibraryPage
    ├─→ SearchPage
    ├─→ FavoritesPage
    ├─→ SettingsPage
    └─→ Media Details Pages
        ├─→ MovieDetailsPage → MediaPlayerPage
        ├─→ SeasonDetailsPage → MediaPlayerPage
        ├─→ AlbumDetailsPage → MediaPlayerPage
        ├─→ ArtistDetailsPage → AlbumDetailsPage
        ├─→ PersonDetailsPage → Media Details
        └─→ CollectionDetailsPage → Media Details
```

### Navigation Rules
1. **Back Button**: Available on all pages
2. **Deep Links**: Detail pages can navigate to other detail pages
3. **Player Return**: MediaPlayerPage returns to the originating page
4. **Navigation Stack**: Cleaned when MediaPlayerPage accumulates or circular navigation detected

## App Lifecycle Notes
- **Backgrounding**: When the app enters background, it saves state and exits unless music is playing.
- **Music Playback**: MusicPlayerService continues playback when allowed by OS; video playback is stopped/paused on background transitions.

## Server Communication

The application communicates with Jellyfin servers using the Jellyfin C# SDK:

### Connection Flow
1. **Server Connection**: User enters server URL manually
2. **Authentication**: Username/password or Quick Connect
3. **Token Storage**: Access token stored securely in Windows Credential Vault
4. **Request Headers**: All requests include authentication token

### Data Flow
```
View → ViewModel → Service → JellyfinApiClient → Jellyfin Server
                     ↓
View ← ViewModel ← Service ← Response Data ←
```

### Key Communication Points
- **JellyfinApiClient**: Main communication interface (singleton)
- **JellyfinSdkSettings**: Configuration including server URL and token
- **RetryHelper**: Automatic retry for failed requests
- **BaseService**: Common error handling for all services

### Common Server Calls
1. **Authentication**: `/Users/AuthenticateByName`
2. **User Data**: `/Users/Me`
3. **Libraries**: `/UserViews`
4. **Items**: `/Items` (with various query parameters)
5. **Playback**: `/PlaybackInfo`, progress reporting
6. **Images**: `/Items/{id}/Images/{type}`

## Key Concepts

### Service Lifetime
- **Singleton**: Services that maintain state
  - AuthenticationService, NavigationService
  - UnifiedDeviceService, ErrorHandlingService
  - UserDataService, CacheManagerService
  - MediaPlaybackService, MediaControlService
- **Transient**: ViewModels and stateless services
  - All ViewModels
  - MediaControllerService, DialogService

### Data Caching
- **CacheManagerService**: In-memory LRU cache with size limits
- **ICacheProvider**: Interface for different caching strategies
- **FileCacheProvider**: Disk-based caching for images
- **GenreCacheService**: Specialized genre data cache
- **Image Caching**: Handled by CachedImage control with FileCacheProvider

### Xbox Optimization
- **Controller Input**: Event-based gamepad input handling via MediaControllerService
- **Focus Management**: Automatic focus handling
- **Performance**: Memory monitoring and optimization
- **Media Codecs**: Xbox-specific codec and HDR support
  - Direct Play: H.264, H.265/HEVC (One S/X+), VP9, AV1 (Series S/X)
  - HDR: HDR10 (One S/X+), Dolby Vision Profile 8.1 (Series S/X only)
  - Audio: AAC, MP3, FLAC, ALAC, AC3, PCM, WMA

### Error Handling
- **Centralized**: All error handling through ErrorHandlingService
- **Retry Logic**: Automatic retry with exponential backoff
- **Error Categories**: User, Network, System, Media, Authentication, Validation, Configuration
- **User Feedback**: Context-aware messages via DialogService
- **Base Class Integration**: Error handling methods in BaseService (HandleErrorAsync, HandleErrorWithDefaultAsync), BaseViewModel (LoadDataCoreAsync pattern), and BaseControl (HandleError, HandleErrorAsync)

## Controller Architecture

The application provides comprehensive Xbox controller support through a layered architecture. See [CONTROLLER_ARCHITECTURE.md](CONTROLLER_ARCHITECTURE.md) for detailed documentation.

### Core Components

#### UnifiedDeviceService
- **Purpose**: Platform abstraction and gamepad management
- **Responsibilities**: Xbox detection, controller state tracking, hardware capabilities
- **Location**: `/Services/UnifiedDeviceService.cs`

#### MediaControllerService  
- **Purpose**: Media playback control via gamepad
- **Responsibilities**: Button-to-action mapping, playback control, UI awareness
- **Location**: `/Services/MediaControllerService.cs`

#### BasePage
- **Purpose**: Controller support for all pages
- **Responsibilities**: Lifecycle management, back navigation, focus helpers
- **Location**: `/Views/BasePage.cs`

#### ControllerInputHelper
- **Purpose**: UI control configuration utilities
- **Responsibilities**: XY focus setup, control optimization, focus management  
- **Location**: `/Controls/ControllerInputHelper.cs`

### Controller Integration Flow

1. **App Startup**: UnifiedDeviceService detects Xbox and monitors gamepads
2. **Page Navigation**: BasePage configures controller support automatically
3. **User Input**: Button presses flow through appropriate services
4. **Media Playback**: MediaControllerService handles playback controls

### Button Mappings (Media Player)

| Button | Action |
|--------|--------|
| A | Play/Pause |
| B | Navigate Back |
| Y | Show Stats |
| D-pad Up/Down | Show/Hide Controls |
| D-pad Left | Skip Back 10 seconds |
| D-pad Right | Skip Forward 30 seconds |
| Left Trigger | Skip Back 10 minutes |
| Right Trigger | Skip Forward 10 minutes |

### Best Practices

1. **All pages must inherit from BasePage** for automatic controller support
2. **Set initial focus** using `SetInitialFocus()` in `InitializePageAsync`
3. **Test navigation paths** with controller only (no mouse/touch)
4. **Handle back button** by overriding `HandleBackNavigation` if needed

For complete controller documentation, see [CONTROLLER_ARCHITECTURE.md](CONTROLLER_ARCHITECTURE.md).

## Development Guide

### Adding a New Page
1. Create View in `/Views` (must inherit from BasePage or DetailsPage for media details)
2. Create ViewModel in `/ViewModels` (must inherit from BaseViewModel)
3. Register ViewModel in `App.xaml.cs` as Transient
4. Use NavigationService.Navigate() or NavigateToItemDetails() for navigation
5. Update navigation flow documentation

#### BasePage Pattern Example:
```csharp
public sealed partial class NewPage : BasePage
{
    // Specify ViewModel type for automatic initialization
    protected override Type ViewModelType => typeof(NewViewModel);
    
    // Typed property for easy access
    public NewViewModel ViewModel => (NewViewModel)base.ViewModel;
    
    public NewPage() : base(typeof(NewPage))
    {
        this.InitializeComponent();
    }
    
    protected override async Task InitializePageAsync(object parameter)
    {
        // Set initial focus for controller
        SetInitialFocus(MyButton);
        await base.InitializePageAsync(parameter);
    }
}
```

### Adding a New Service
1. Define interface in `ServiceInterfaces.cs`
2. Implement in `/Services`
3. Register in `App.xaml.cs` (choose appropriate lifetime)
4. Inject into ViewModels as needed

### Working with Media Items
- Use `BaseItemDto` from Jellyfin SDK
- Access images via `ImageUrlHelper`
- Navigate via `NavigationService.NavigateToItemDetails()`
- Handle playback through `MediaPlaybackService`
- Toggle favorites/watched via `UserDataService`

### Xbox Controller Support
- All pages inherit from BasePage for automatic controller support
- Use `ControllerInputHelper` for additional control configuration
- Test with Xbox controller connected
- See [Controller Architecture](#controller-architecture) for details

### Testing Considerations
- Test on Xbox Series S/X and Xbox One
- Verify memory usage stays within limits
- Check network retry behavior
- Validate controller navigation paths

### Common Patterns
1. **Loading States**: Use BaseViewModel's IsLoading/IsRefreshing properties
2. **Error Handling**: Use ErrorHandlingService through base class methods
3. **Navigation**: Always use NavigationService.NavigateToItemDetails()
4. **Data Binding**: Use x:Bind for performance
5. **Async Operations**: Use ConfigureAwait(false) in services
6. **Data Loading**: Override LoadDataCoreAsync in ViewModels
7. **User Data**: Use UserDataService for favorites/watched
8. **Settings**: Settings ViewModels inherit from BaseViewModel and use PreferencesService

### Performance Guidelines
- Minimize memory allocations
- Use virtualization for large lists
- Cache expensive operations
- Dispose resources properly
- Monitor with SystemMonitorService

### Debugging Tips
- Check debug output for service initialization
- Use logging in ViewModels and Services
- Monitor network calls in Fiddler
- Test offline scenarios
- Verify token refresh behavior
