# Architecture and Design Patterns

## Architecture Overview

Gelatinarm follows the Model-View-ViewModel (MVVM) pattern with a service layer:

```
┌─────────────────────────────────────────────────────────┐
│                    Views (XAML)                         │
│  User Interface - Pages, Controls, Visual Elements      │
└────────────────────────┬────────────────────────────────┘
                         │ Data Binding
┌────────────────────────▼────────────────────────────────┐
│                  ViewModels (C#)                        │
│  Presentation Logic - Commands, Properties, State       │
└────────────────────────┬────────────────────────────────┘
                         │ Dependency Injection
┌────────────────────────▼────────────────────────────────┐
│                   Services (C#)                         │
│  Business Logic - Data Access, Operations, Caching      │
└────────────────────────┬────────────────────────────────┘
                         │ 
┌────────────────────────▼────────────────────────────────┐
│              Jellyfin SDK / External APIs               │
│  Server Communication - HTTP Requests, Responses        │
└─────────────────────────────────────────────────────────┘
```

## Core Design Patterns

### 1. MVVM (Model-View-ViewModel)
- **Views**: XAML files defining UI layout
  - All pages inherit from `BasePage` which provides lifecycle management and services
  - Detail pages inherit from `DetailsPage` for common media functionality
- **ViewModels**: Coordinate between Services and Views
  - Inherit from `BaseViewModel` (which extends ObservableObject)
  - Automatically initialized by BasePage when `ViewModelType` is specified
  - Handle UI state, filtering, and command execution
- **Data Binding**: Mix of x:Bind (compile-time) and Binding (runtime)
- **Commands**: ICommand implementation for user actions

### 2. Dependency Injection
- **Container**: Built in App.xaml.cs using Microsoft.Extensions.DependencyInjection
- **Service Registration Order**:
  1. Logging configuration
  2. Core services (PreferencesService, UnifiedDeviceService)
  3. HTTP client and Jellyfin SDK setup
  4. Authentication and user services
  5. Media services
  6. ViewModels
- **Lifetime Management**:
  - **Singleton**: Stateful services that maintain data across app lifetime
    - INavigationService, IAuthenticationService, IUserProfileService
    - IMediaPlaybackService, IMediaControlService, IErrorHandlingService
    - MainViewModel (persists home screen data)
  - **Transient**: Stateless services and most ViewModels
    - All detail page ViewModels (MovieDetailsViewModel, etc.)
    - Settings ViewModels
    - IMediaControllerService, IDialogService
- **Constructor Injection**: All dependencies resolved via constructors
- **Service Resolution**: Use GetService<T>() or GetRequiredService<T>() from BasePage

### 3. Service Layer Pattern
- **Interfaces**: Define contracts (mostly in ServiceInterfaces.cs)
- **Implementations**: Concrete classes in Services folder
- **Multiple Responsibilities**: Some services like MediaPlaybackService handle many related operations
- **Data Access**: Services handle all API calls
- **Caching Layer**: CacheManagerService provides in-memory cache
- **Abstraction**: ViewModels don't directly call APIs

### 4. Observer Pattern
- **Events**: Services raise events for state changes
- **INotifyPropertyChanged**: ViewModels notify Views
- **Disposal Pattern**: ViewModels and services implement IDisposable for cleanup

### 5. Media Playback Architecture
- **MediaPlaybackService**: High-level orchestration and session management
  - Handles playback initiation and navigation
  - Manages playback sessions and queue
  - Routes video to MediaPlayerPage, audio to MusicPlayerService
- **MediaControlService**: Low-level MediaPlayer control
  - Direct control of Windows.Media.Playback.MediaPlayer
  - Handles play, pause, seek operations
  - Manages playback state events
- **PlaybackControlService**: Playback setup, stream selection, and restart flow
  - Delegates source selection to `PlaybackSourceResolver`
  - Delegates resume logic to `PlaybackResumeCoordinator`
  - Delegates restart logic for track/subtitle changes to `PlaybackRestartService`
  - Uses stream policy to align DirectPlay and HLS behavior with shared logic
- **Playback Resume / Buffering Orchestration** (Helpers)
  - `PlaybackStateOrchestrator`: reacts to PlaybackStateChanged and updates UI-facing state
  - `BufferingStateCoordinator`: standardizes buffering start/end transitions and timeout handling
  - `ResumeFlowCoordinator`: centralizes resume acceptance and completion reporting
  - `SeekCompletionCoordinator`: handles seek completion logging and validation
- **Separation of Concerns**: orchestration (ViewModel + helpers) is kept distinct from control (services)

### 6. Error Handling Pattern
- **ErrorHandlingService**: Centralized error processing
- **ErrorContext**: Contextual information about errors
- **Category-based handling**: Different strategies for different error types
- **User-friendly messages**: Automatic conversion of technical errors

## Application Startup Flow

### Initialization Sequence (App.xaml.cs)
1. **App Constructor**: Suspension handlers and ConfigureServices()
2. **ConfigureServices**: Register all dependencies with DI container
   - Logging, HTTP client, Jellyfin SDK
   - Services registered as Singleton or Transient
3. **OnLaunched**: Application activation
   - InitializeCoreServices() - Get service references
   - Wire up MusicPlayerService to MediaPlaybackService
   - Create/reuse RootContainer with navigation frame
4. **Navigation Decision**:
   - Check authentication state
   - Navigate to appropriate initial page
5. **Service Initialization**: Services initialize on first use

### Service Initialization Approach
- **Graceful Degradation**: App continues even if service initialization fails
- **Core Services**: Retrieved in InitializeCoreServices() but failures don't stop app
  - AuthenticationService, NavigationService, ErrorHandlingService
- **Runtime Resolution**: Most services resolved when needed via GetService<T>()
- **Failure Logging**: Failed services logged but app continues
- **Service Locator**: Helpers and converters use `ServiceLocator` when constructor injection is not available
  - Cache frequently used services in controls/pages to avoid repeated lookups

## Key Architectural Decisions

### Page Caching Strategy
- **Enabled caching**: SeasonDetailsPage, LibraryPage (frequently revisited)
- **Disabled caching**: PersonDetailsPage, AlbumDetailsPage (fresh data each visit)
- State preserved only for cached pages
- Trade-off between memory usage and performance

### Dependency Injection Strategy
- Most dependencies injected via constructors
- Limited service locator usage (ImageHelper static cache)
- Testable architecture for most components

### Async/Await Throughout
- All I/O operations async
- ConfigureAwait(false) used in service methods
- UI thread protection via UIHelper.RunOnUIThreadAsync

### Error Handling Strategy
```
View (User Message) ← ViewModel (Process) ← Service (Return Default) ← Network (Error)
```
- Services use ErrorHandler.HandleErrorAsync and return default values
- ViewModels process results and update UI state
- Views display user-friendly messages via DialogService

### Memory Management
- Explicit disposal of resources (IDisposable pattern)
- Event handler cleanup in Dispose methods
- Cache size limits (100MB default for CacheManagerService)

## Component Responsibilities

### Views
- UI layout and styling
- User input handling (delegated to MediaControllerService for media playback)
- Data binding to ViewModels
- Navigation triggers

### ViewModels
- UI state management
- Command handlers
- Data transformation for display
- Service orchestration

### Services
- Business operations
- External communication
- Data caching
- Cross-cutting concerns

### Models
- Data structures
- No behavior
- Serialization attributes
- Validation rules

## Communication Patterns

### View to ViewModel
```csharp
// View (XAML)
<Button Command="{x:Bind ViewModel.PlayCommand}" />

// ViewModel
public ICommand PlayCommand { get; }
```

### ViewModel to Service
```csharp
// ViewModel
private async Task LoadDataAsync()
{
    var data = await _mediaService.GetItemsAsync();
    Items = new ObservableCollection<Item>(data);
}
```

### Service Events
```csharp
// Service
public event EventHandler<DataChangedEventArgs> DataChanged;

// ViewModel subscription
_service.DataChanged += OnDataChanged;
```

## Coding Standards

### Naming Conventions
- **Classes**: PascalCase
- **Interfaces**: IPascalCase
- **Private fields**: _camelCase
- **Properties**: PascalCase
- **Methods**: PascalCase
- **Parameters**: camelCase
- **Base Classes**: Only ONE class per folder should have "Base" prefix (e.g., BaseViewModel, BaseService, BasePage)
- **Specialized Classes**: Don't use "Base" prefix for specialized inheritance (e.g., DetailsViewModel, not BaseDetailsViewModel)

### File Organization
- One class per file
- Matching file and class names
- Logical folder structure
- Related files grouped

### Method Structure
```csharp
public async Task<Result> MethodNameAsync(Parameter param)
{
    // Validation
    if (param == null) throw new ArgumentNullException(nameof(param));
    
    try
    {
        // Core logic
        return await Operation().ConfigureAwait(false);
    }
    catch (Exception ex)
    {
        Logger.LogError(ex, "Operation failed");
        throw;
    }
}
```

### XAML Guidelines
- Use x:Bind over Binding
- Name only required elements
- Extract complex templates
- Consistent spacing and indentation

### Compiler Warning Suppressions

The project suppresses certain compiler warnings that are either benign or unavoidable:

#### CS1998 - Async method lacks await operators
- **Reason**: Some async method overrides don't require await but must match base signatures
- **Example**: Base class virtual async methods that derived classes may not need to await

#### CS0108 - Member hides inherited member
- **Reason**: XAML-generated code creates fields for x:Name elements that hide base class properties
- **Details**: DetailsPage uses FindName() to locate common UI elements (LoadingOverlay, MessageDialog, etc.)
- **Impact**: Harmless - the base class properties use FindName() at runtime, not compile-time fields
- **Example**: Derived detail pages define `x:Name="LoadingOverlay"` which generates a field that hides the base class property

## Performance Considerations

### UI Virtualization
- GridView and ListView virtualize by default
- LibraryViewModel supports LoadMoreItems command for paging
- Images load on-demand via CachedImage control
- Mix of x:Bind and Binding based on requirements
- Set `x:DataType` on DataTemplates for better performance

### Data Caching
- **CacheManagerService**: LRU cache with configurable size limits (100MB default)
- **FileCacheProvider**: Disk-based image caching
- **GenreCacheService**: Specialized genre data caching
- **Cache Expiration Times**:
  - Main view cache: 5 minutes (MAIN_VIEW_CACHE_EXPIRATION_MINUTES)
  - Media discovery: 2 minutes (MEDIA_DISCOVERY_CACHE_MINUTES)
  - Default varies by data type
- Memory-based caching with automatic cleanup

### Network Optimization
- Retry with exponential backoff (3 attempts by default)
- HTTP request timeout: 10 seconds (RetryConstants.HTTP_REQUEST_TIMEOUT_SECONDS)
- Search timeout: 4 seconds for quick feedback
- Initial retry delay: 1000ms with exponential backoff
- Maximum retry delay: 30 seconds
- Image loading: Progressive with retry on failure

### Memory Management (Xbox One: 3GB limit)
- **Monitor Usage**: SystemMonitorService tracks memory
- **Image Optimization**:
  - Images requested with quality=90
  - FileCacheProvider for disk-based caching
  - CacheManagerService: 100MB default limit
- **Collection Management**:
  - ObservableCollection.ReplaceAll() for bulk updates
  - GridView/ListView use virtualization by default
  - Dispose media players when not in use
- **Page State**:
  - NavigationCacheMode.Enabled for frequently visited pages
  - Clear navigation stack on logout

### Input Handling
- Event-based gamepad input (no polling)
- MediaControllerService handles all media playback input
- XY focus navigation disabled during playback to prevent analog stick sounds
- Control visibility state determines input behavior
- Debounce search input (500ms delay)

### Startup Optimization
- Lazy load services where possible
- Defer non-critical initialization
- Preload essential data in App.xaml.cs
- Use SplashScreen extended for long operations

### Media Playback Performance
- **Adaptive Bitrate**: MediaOptimizationService selects quality
- **Buffer Management**: 30 second buffer for smooth playback
- **Codec Priority**: Direct Play > Direct Stream > Transcode
- **Video Codecs**: H.264, H.265/HEVC, VP9, VP8, AV1 (Series S/X), MPEG-2, VC-1
- **Audio**: AAC, MP3, FLAC, AC3/EAC3, DTS passthrough
- **HDR Support**:
  - Xbox One S/X: HDR10, HDR10+, HLG
  - Xbox Series S/X: HDR10, HDR10+, HLG, Dolby Vision Profile 8.1
- **Transcode Triggers**: Unsupported codec, bandwidth limits, or incompatible profile

### Playback Statistics System

The PlaybackStatisticsService provides real-time metrics during media playback, displaying accurate information from multiple data sources:

#### Progress Reporting
- **Interval**: Reports playback progress to Jellyfin server every 5 seconds (configurable via POSITION_REPORT_INTERVAL_TICKS)
- **Timeout Handling**: 10-second timeout for HTTP requests with proper completion tracking
- **Overlap Prevention**: Ensures new progress reports don't start while previous ones are pending
- **Network Resilience**: Handles slow networks gracefully by waiting for timed-out requests to complete

#### Data Sources
- **MediaSourceInfo** (from Jellyfin server):
  - Play Method (Direct Play/Direct Stream/Transcode)
  - Container format (MKV, MP4, etc.)
  - Video/Audio codec information
  - Bitrate (displayed in Mbps)
  - Media streams metadata

- **MediaPlayer.PlaybackSession** (Windows Media Player):
  - Resolution (NaturalVideoWidth/Height)
  - Current position and duration
  - Playback state (Playing/Paused/Buffering)
  - Buffer and download progress percentages
  - Playback speed (if not 1.0x)

- **MediaStreams** (within MediaSourceInfo):
  - Video codec and profile
  - HDR type (HDR10, Dolby Vision, SDR)
  - Frame rate (real or average fps)
  - Audio codec, channels, and sample rate
  - Color transfer characteristics

#### Statistics Displayed
- **Video Info**: Resolution, codec, frame rate, playback speed
- **Audio Info**: Codec, channels (with description like "6 (5.1)"), sample rate
- **Playback Info**: Player type, play method, protocol (HLS/DASH/HTTP), position
- **Network Info**: Current playback state, bitrate
- **Buffer Info**: Buffer percentage, download progress (when available)

All statistics are pulled from real data sources with no hardcoded values. Stats update 4 times per second (250ms interval).

## Testing Approach

### Unit Testing
- Services tested in isolation
- Mock dependencies
- Test business logic

### Integration Testing
- Service integration
- Navigation flows
- Data persistence

### UI Testing
- Page load performance
- Navigation paths
- Error scenarios

### Platform Testing
- Xbox One (3GB memory)
- Xbox Series S/X
- Windows 10/11
- Different screen resolutions

## Error Handling Architecture

### Unified Error Handling
The application uses a centralized ErrorHandlingService for consistent error processing:

#### Components
1. **ErrorHandlingService**: Central service that processes all errors
2. **ErrorContext**: Provides context about where and what type of error occurred
3. **ErrorCategory**: Categorizes errors (User, Network, System, Media, Authentication, Validation, Configuration)
4. **ErrorSeverity**: Indicates severity (Info, Warning, Error, Critical)

#### Integration Points
- **BaseService**: All services inherit error handling via `HandleErrorAsync` and `HandleErrorWithDefaultAsync`
- **BaseViewModel**: All ViewModels inherit error handling capabilities via `ErrorHandler` property and `LoadDataCoreAsync` pattern
- **BaseControl**: All controls inherit standardized error handling via `HandleError` and `HandleErrorAsync`

#### Error Flow
```
Exception Occurs → ErrorContext Created → ErrorHandlingService Processes → 
    ↓
    ├─→ Logs with appropriate level
    ├─→ Determines if user should see message
    ├─→ Generates user-friendly message
    └─→ Shows dialog via IDialogService if appropriate
```

#### Error Handling Methods

Both synchronous and asynchronous error handling are supported:

**In Services (inheriting from BaseService):**
```csharp
// Using try/catch with ErrorHandler
public async Task<T> GetDataAsync<T>()
{
    var context = CreateErrorContext("GetData", ErrorCategory.Network);
    try
    {
        return await _apiClient.GetAsync<T>();
    }
    catch (Exception ex)
    {
        return await ErrorHandler.HandleErrorAsync<T>(ex, context, defaultValue, showUserMessage: false);
    }
}

// Using RetryAsync for automatic retry with exponential backoff
public async Task<UserDto> LoadUserProfileAsync(CancellationToken cancellationToken)
{
    return await RetryAsync(
        async () => await _apiClient.Users.Me.GetAsync(null, cancellationToken),
        cancellationToken: cancellationToken
    ).ConfigureAwait(false);
}
```

**In ViewModels (inheriting from BaseViewModel):**
```csharp
// Override LoadDataCoreAsync - errors handled automatically by BaseViewModel
protected override async Task LoadDataCoreAsync(CancellationToken cancellationToken)
{
    var data = await _service.GetDataAsync(cancellationToken);
    await RunOnUIThreadAsync(() => Items.ReplaceAll(data));
}

// Or use try/catch with ErrorHandler directly
private async Task CustomOperationAsync()
{
    var context = CreateErrorContext("CustomOperation", ErrorCategory.User);
    try
    {
        await _service.DoSomethingAsync();
    }
    catch (Exception ex)
    {
        await ErrorHandler.HandleErrorAsync(ex, context, showUserMessage: true);
    }
}
```

#### Best Practices
1. Services should use try/catch with `ErrorHandler.HandleErrorAsync<T>` and return default values
2. ViewModels can override `LoadDataCoreAsync` for automatic error handling
3. Always use ErrorCategory to classify errors appropriately
4. Let ErrorHandlingService generate user messages
5. Use showUserMessage parameter wisely (true for user-initiated actions)
6. Don't catch exceptions just to log them - use the service
7. Maintain error context through the call stack
8. NEVER show technical error messages directly to users

## UI Thread Execution

### Unified UI Thread Helpers
All UI thread execution is consolidated through the UIHelper class:

#### Available Methods
```csharp
// Synchronous action
await UIHelper.RunOnUIThreadAsync(Action action, CoreDispatcher dispatcher = null, ILogger logger = null)

// Synchronous function with result
await UIHelper.RunOnUIThreadAsync<T>(Func<T> func, CoreDispatcher dispatcher = null, ILogger logger = null)

// Async action
await UIHelper.RunOnUIThreadAsync(Func<Task> asyncAction, CoreDispatcher dispatcher = null, ILogger logger = null)

// Async function with result
await UIHelper.RunOnUIThreadAsync<T>(Func<Task<T>> asyncFunc, CoreDispatcher dispatcher = null, ILogger logger = null)

// With priority
await UIHelper.RunOnUIThreadAsync(Action action, CoreDispatcherPriority priority, CoreDispatcher dispatcher = null, ILogger logger = null)
```

#### Usage Patterns
1. **In ViewModels** (inherit from BaseViewModel):
   ```csharp
   await RunOnUIThreadAsync(() => 
   {
       // Update UI properties
   });
   ```

2. **In Services**:
   ```csharp
   await UIHelper.RunOnUIThreadAsync(() => 
   {
       // Raise events or update UI
   }, dispatcher, Logger);
   ```

3. **In Controls/Views**:
   ```csharp
   await UIHelper.RunOnUIThreadAsync(() => 
   {
       // Update UI elements
   }, Dispatcher, _logger);
   ```

4. **Fire-and-Forget** (use sparingly):
   ```csharp
   FireAndForget(() => UIHelper.RunOnUIThreadAsync(() =>
   {
       // Non-critical UI updates
   }, dispatcher, logger));
   ```

#### Best Practices
1. NEVER use CoreDispatcher.RunAsync directly
2. Always provide logger parameter for error tracking
3. Use fire-and-forget pattern only for non-critical operations
4. Services requiring DispatcherTimer may keep CoreDispatcher parameter

## Dialog Service Architecture

### Unified Dialog Handling
All user dialogs must go through IDialogService:

#### Available Methods
```csharp
// Information message
await dialogService.ShowMessageAsync(string message, string title = null)

// Error message
await dialogService.ShowErrorAsync(string message, string title = "Error")

// Confirmation dialog
bool result = await dialogService.ShowConfirmationAsync(string message, string title = "Confirm", string primaryButtonText = "Yes", string secondaryButtonText = "No")
```

#### Usage Rules
1. **NEVER** create ContentDialog or MessageDialog directly
2. **ALWAYS** use IDialogService methods
3. **EXCEPTION**: Critical errors in App.xaml.cs only
4. Error messages should be user-friendly (no technical details)
5. DialogService handles UI thread marshaling internally

## Security Considerations

### Authentication
- Tokens in secure storage
- No credentials in memory
- Automatic token refresh

### Data Protection
- HTTPS only
- Certificate validation
- No sensitive data in logs

### Input Validation
- Server URL validation
- User input sanitization
- Command parameter validation

## Extensibility Points

### Adding New Media Types
1. Create detail page View/ViewModel inheriting from DetailsPage
2. Update NavigationService.NavigateToItemDetails method
3. Register ViewModel in App.xaml.cs
4. Handle in search results

### Custom Services
1. Define interface
2. Implement service
3. Register in container
4. Inject where needed

### UI Themes
- Resources in App.xaml
- Override in page resources
- Consistent use of theme resources

### Platform Support
- Abstracted device service
- Platform-specific implementations
- Capability detection
