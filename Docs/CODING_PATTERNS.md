# Gelatinarm Coding Patterns

This document describes the established coding patterns in the Gelatinarm codebase after the consolidation effort. These patterns ensure consistency, reduce duplication, and improve maintainability.

## Table of Contents
1. [Error Handling Pattern](#error-handling-pattern)
2. [Navigation Pattern](#navigation-pattern)
3. [User Data Operations Pattern](#user-data-operations-pattern)
4. [Data Loading Pattern](#data-loading-pattern)
5. [Settings Management Pattern](#settings-management-pattern)
6. [Page Lifecycle Pattern](#page-lifecycle-pattern)
7. [Service Architecture Pattern](#service-architecture-pattern)
8. [Controller Input Pattern](#controller-input-pattern)
9. [Temporary Workarounds](#temporary-workarounds)

## Error Handling Pattern

### Overview
All error handling is centralized through the `ErrorHandlingService` to provide consistent error messages, logging, and user feedback.

### Implementation

#### In Services (inheriting from BaseService)
```csharp
public async Task<Result> DoSomethingAsync()
{
    var context = CreateErrorContext("DoSomething", ErrorCategory.Network);
    return await ExecuteWithErrorHandlingAsync(
        async () => 
        {
            // Your actual implementation
            return await _apiClient.GetDataAsync();
        },
        context,
        showUserMessage: true
    );
}
```

#### In ViewModels (inheriting from BaseViewModel)
```csharp
private async Task LoadDataAsync()
{
    await ExecuteWithErrorHandlingAsync(
        async () => 
        {
            var data = await _service.GetDataAsync();
            Items.ReplaceAll(data);
        },
        CreateErrorContext("LoadData", ErrorCategory.Network)
    );
}
```

#### Direct Service Usage (Async)
```csharp
try
{
    // Operation
}
catch (Exception ex)
{
    var errorService = GetService<IErrorHandlingService>();
    var context = new ErrorContext("OperationName", "ClassName", ErrorCategory.System);
    await errorService.HandleErrorAsync(ex, context, showUserMessage: true);
}
```

#### Direct Service Usage (Synchronous/Fire-and-Forget)
```csharp
// For synchronous contexts or when dialog display should be fire-and-forget
var context = CreateErrorContext("ResumePlayback", ErrorCategory.Media, ErrorSeverity.Error);
var exception = new ResumeStuckException(currentPos, targetPos, attemptCount);
ErrorHandler?.HandleError(exception, context, showUserMessage: true);
```

### Error Categories
- `User`: User input errors, validation failures
- `Network`: Connection issues, API failures
- `System`: Platform errors, unexpected failures
- `Media`: Playback errors, codec issues
- `Authentication`: Login failures, token expiration
- `Validation`: Data validation errors
- `Configuration`: Settings or setup issues

### Custom Exception Types
Located in `Models/ErrorHandling.cs`:
- `ResumeStuckException`: Thrown when media playback cannot resume at a saved position
- `ResumeTimeoutException`: Thrown when resume operation exceeds time limit

These exceptions provide user-friendly messages that are displayed directly to users.

### Key Benefits
- Consistent error messages across the app
- Automatic retry logic for network errors
- Contextual user messages
- Centralized logging
- No duplicate error handling code

## Navigation Pattern

### Overview
All navigation is centralized through `NavigationService`, with special handling for media items via `NavigateToItemDetails`.

### Implementation

#### Navigate to Media Item
```csharp
// From any ViewModel or Page
NavigationService.NavigateToItemDetails(baseItemDto);
```

#### Navigate to Standard Page
```csharp
// Navigate with parameter
NavigationService.Navigate(typeof(SettingsPage), parameter);

// Navigate without parameter
NavigationService.Navigate(typeof(SearchPage));
```

#### Navigation Rules
1. **Never use Frame.Navigate directly**
2. **Always use NavigationService methods**
3. **Media items use NavigateToItemDetails**
4. **MusicPlayer handled automatically for audio**

### Removed Anti-Patterns
```csharp
// DON'T: Custom navigation methods in ViewModels
private void NavigateToMovie(BaseItemDto movie) { }

// DON'T: Direct frame navigation
Frame.Navigate(typeof(MovieDetailsPage), movie);

// DON'T: NavigationHelper static methods
NavigationHelper.NavigateToItemDetails(item);
```

## User Data Operations Pattern

### Overview
All favorite and watched status operations go through `UserDataService` to ensure consistency and proper state management.

### Implementation

#### Toggle Favorite Status
```csharp
public class MyViewModel : BaseViewModel
{
    private readonly IUserDataService _userDataService;
    
    public async Task ToggleFavoriteAsync()
    {
        await _userDataService.ToggleFavoriteAsync(ItemId);
        // UI updates automatically via data binding
    }
}
```

#### Toggle Watched Status
```csharp
await _userDataService.ToggleWatchedAsync(ItemId);
```

#### Batch Operations
```csharp
// Mark multiple items as favorites
var itemIds = Items.Select(i => i.Id).ToList();
await _userDataService.SetFavoriteStatusAsync(itemIds, isFavorite: true);
```

### Integration with UI
```xml
<!-- UserDataButtons control handles everything -->
<controls:UserDataButtons 
    ItemId="{x:Bind ViewModel.Item.Id}"
    IsFavorite="{x:Bind ViewModel.Item.UserData.IsFavorite}"
    IsWatched="{x:Bind ViewModel.Item.UserData.Played}" />
```

### Removed Anti-Patterns
```csharp
// DON'T: Implement toggle methods in ViewModels
private async Task ToggleFavoriteAsync() { }

// DON'T: Call Jellyfin API directly
await _jellyfinApiClient.UpdateFavoriteStatusAsync(...);
```

## Data Loading Pattern

### Overview
Standardized data loading with built-in caching, refresh support, and state management through `BaseViewModel`.

### Implementation

#### Basic Data Loading
```csharp
public class LibraryViewModel : BaseViewModel
{
    protected override async Task<LoadResult> LoadDataCoreAsync(
        bool forceRefresh, 
        CancellationToken cancellationToken)
    {
        // Check cache unless forcing refresh
        if (!forceRefresh && _cachedData != null)
        {
            return LoadResult.Success(_cachedData);
        }
        
        // Load fresh data
        var items = await _mediaService.GetLibraryItemsAsync(cancellationToken);
        _cachedData = items;
        
        // Update UI
        Items.ReplaceAll(items);
        
        return LoadResult.Success(items);
    }
}
```

#### Refresh Pattern
```csharp
protected override async Task RefreshDataCoreAsync(CancellationToken cancellationToken)
{
    // Custom refresh logic if different from load
    // Otherwise just call load with forceRefresh
    await LoadDataCoreAsync(true, cancellationToken);
}
```

#### Usage in Pages
```csharp
protected override async Task InitializePageAsync(object parameter)
{
    var viewModel = (LibraryViewModel)ViewModel;
    await viewModel.LoadDataAsync(); // Handles all states
}

// Pull-to-refresh
private async void OnRefreshRequested()
{
    await ViewModel.RefreshAsync();
}
```

### State Properties
- `IsLoading`: True during initial load
- `IsRefreshing`: True during refresh
- `HasData`: True when data is loaded
- `IsError`: True if loading failed
- `LastDataLoad`: Timestamp of last successful load

## Settings Management Pattern

### Overview
Settings ViewModels inherit from `BaseSettingsViewModel` for consistent load/save operations and validation.

### Implementation

```csharp
public class PlaybackSettingsViewModel : BaseSettingsViewModel
{
    private int _defaultQuality;
    
    public int DefaultQuality
    {
        get => _defaultQuality;
        set 
        { 
            if (SetProperty(ref _defaultQuality, value))
            {
                HasUnsavedChanges = true;
            }
        }
    }
    
    protected override async Task LoadSettingsAsync()
    {
        DefaultQuality = await PreferencesService.GetAsync<int>(
            PreferenceConstants.DEFAULT_QUALITY, 
            1080);
    }
    
    protected override async Task SaveSettingsAsync()
    {
        await PreferencesService.SetAsync(
            PreferenceConstants.DEFAULT_QUALITY, 
            DefaultQuality);
        
        HasUnsavedChanges = false;
    }
    
    protected override bool ValidateSettings()
    {
        return DefaultQuality > 0 && DefaultQuality <= 4320;
    }
}
```

### Features
- Automatic loading on navigation
- Change tracking via `HasUnsavedChanges`
- Built-in validation support
- Consistent save/cancel operations

## Page Lifecycle Pattern

### Overview
All pages inherit from `BasePage` for consistent lifecycle management, service access, and controller support.

### Implementation

```csharp
public sealed partial class LibraryPage : BasePage
{
    // Specify ViewModel type for automatic initialization
    protected override Type ViewModelType => typeof(LibraryViewModel);
    
    // Typed property for convenience
    public LibraryViewModel ViewModel => (LibraryViewModel)base.ViewModel;
    
    public LibraryPage() : base(typeof(LibraryPage))
    {
        InitializeComponent();
    }
    
    protected override async Task InitializePageAsync(object parameter)
    {
        // Set controller focus
        SetInitialFocus(LibraryGridView);
        
        // Load data
        if (parameter is Guid libraryId)
        {
            await ViewModel.LoadLibraryAsync(libraryId);
        }
    }
    
    protected override async Task RefreshDataAsync(bool forceRefresh)
    {
        if (forceRefresh)
        {
            await ViewModel.RefreshAsync();
        }
    }
    
    protected override void CancelOngoingOperations()
    {
        ViewModel?.CancelOperations();
    }
}
```

### Lifecycle Methods
1. `OnNavigatedTo` → `InitializePageAsync` → `RefreshDataAsync`
2. `OnPageLoadedAsync` (after UI loaded)
3. `OnNavigatedFrom` → `CancelOngoingOperations` → `CleanupResources`
4. `OnPageUnloadedCore` (final cleanup)

### Built-in Features
- Automatic ViewModel initialization
- Service resolution helpers
- Controller input setup
- Back button handling
- Focus management

## Service Architecture Pattern

### Overview
Clear separation of concerns with specialized services for different domains.

### Service Layers

#### 1. Base Services (Infrastructure)
```csharp
public abstract class BaseService
{
    protected ILogger Logger { get; }
    protected IErrorHandlingService ErrorHandler { get; }
    
    // Common error handling methods
    protected Task<T> ExecuteWithErrorHandlingAsync<T>(...);
}
```

#### 2. Domain Services (Business Logic)
```csharp
// Media operations
public class MediaPlaybackService : BaseService, IMediaPlaybackService
{
    // High-level orchestration
    public async Task<PlaybackInfo> StartPlaybackAsync(BaseItemDto item) { }
}

// Direct player control
public class MediaControlService : BaseService, IMediaControlService
{
    // Low-level MediaPlayer control
    public void Play() => _mediaPlayer.Play();
}
```

#### 3. Platform Services (Device-Specific)
```csharp
public class UnifiedDeviceService : BaseService, IUnifiedDeviceService
{
    // Xbox-specific functionality
    public bool IsXboxEnvironment { get; }
    public bool IsControllerConnected { get; }
}
```

### Service Lifetime Rules
```csharp
// Singleton: Stateful services
services.AddSingleton<IAuthenticationService, AuthenticationService>();
services.AddSingleton<INavigationService, NavigationService>();
services.AddSingleton<IMediaPlaybackService, MediaPlaybackService>();

// Transient: Stateless services and ViewModels
services.AddTransient<IMediaControllerService, MediaControllerService>();
services.AddTransient<MainViewModel>();
```

## Media Resume Pattern

### Overview
Resume position is applied when MediaPlayer transitions to Playing state, not using VideoFrameAvailable.

### Implementation
```csharp
// In PlaybackStateChanged handler
if (newState == MediaPlaybackState.Playing)
{
    if (!_hasVideoStarted && !_hasPerformedInitialSeek)
    {
        _hasVideoStarted = true;
        Logger.LogInformation("Video playback started - applying resume position");
        
        // Apply resume position
        var resumeResult = await TryApplyResumePositionAsync();
        if (resumeResult)
        {
            Logger.LogInformation("Applied resume position on playback start");
        }
    }
}
```

### Important Notes
- **DO NOT use VideoFrameAvailable** - It requires `IsVideoFrameServerEnabled=true` which is for frame processing (external subtitles, filters)
- **DO use PlaybackSession.PlaybackState** - Reliable indicator of playback readiness
- **Resume triggers on Playing state** - Video is ready when state transitions to Playing
- **No timeouts needed** - Playing state is deterministic

## Controller Input Pattern

### Overview
Automatic controller support through inheritance and helper utilities.

### Page-Level Setup
```csharp
public sealed partial class SearchPage : BasePage
{
    protected override async Task InitializePageAsync(object parameter)
    {
        // Set initial focus for controller navigation
        SetInitialFocus(SearchBox);
        
        // Configure specific controls if needed
        ControllerInputHelper.ConfigureTextBoxForController(
            SearchBox, 
            InputScopeNameValue.Search, 
            Logger);
    }
}
```

### Media Controller Integration
```csharp
public sealed partial class MediaPlayerPage : BasePage
{
    private IMediaControllerService _controllerService;
    
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        
        _controllerService = GetRequiredService<IMediaControllerService>();
        _controllerService.ActionTriggered += OnControllerAction;
        
        this.KeyDown += OnKeyDown;
    }
    
    private async void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (await _controllerService.HandleKeyDownAsync(e.Key))
        {
            e.Handled = true;
        }
    }
    
    private void OnControllerAction(object sender, MediaAction action)
    {
        switch (action)
        {
            case MediaAction.PlayPause:
                TogglePlayPause();
                break;
            case MediaAction.ShowInfo:
                ShowControls();
                break;
        }
    }
}
```

### Features
- Automatic XY focus navigation
- System back button handling  
- Virtual keyboard management
- Media playback control mapping
- Focus state preservation

## Temporary Workarounds

### HLS Resume Workaround

#### Overview
The application includes workarounds for HLS (HTTP Live Streaming) resume functionality due to current Jellyfin server behavior where the server uses `-noaccurate_seek` for HLS streams, causing resume positions to snap to the nearest keyframe instead of the exact requested position.

#### Workaround Locations
All HLS-specific workarounds are marked with `HLS WORKAROUND` comments for easy identification and removal:

**PlaybackControlService.cs:**
- Line 468-473: Enhanced resume logic routing to `ApplyHlsResumePosition()`
- Line 674+: HLS-specific resume implementation
- Line 763-778: Manifest offset detection and tracking
- Line 119: HlsManifestOffset property (internal setter)

**MediaPlayerViewModel.cs:**
- Line 399-444: Backward seek handling for HLS manifests
- Line 133-155: Position property includes HLS offset
- Line 71-76: HLS tracking variables for manifest changes

**ServiceInterfaces.cs:**
- Line 529: HlsManifestOffset property in IPlaybackControlService interface

#### Removing HLS Workarounds
When server implementation properly handles HLS resume:

1. Search for all `HLS WORKAROUND` comments in the codebase
2. In PlaybackControlService.cs:
   - Remove HLS-specific routing (lines 468-473)
   - Remove `ApplyHlsResumePosition()` method
   - Remove HlsManifestOffset property
3. In MediaPlayerViewModel.cs:
   - Remove backward seek HLS handling
   - Simplify Position getter to return just `_position`
   - Remove HLS tracking variables
4. In ServiceInterfaces.cs:
   - Remove HlsManifestOffset from interface

The tiered resume approach ensures that if the server begins honoring StartTimeTicks properly, client-side workarounds will not be triggered automatically.

## Summary

These patterns were established during the consolidation effort to:

1. **Reduce code duplication** by ~40%
2. **Improve consistency** across the codebase
3. **Simplify maintenance** through centralization
4. **Enhance testability** with clear separation
5. **Standardize error handling** and user feedback
6. **Provide better controller support** automatically

When implementing new features, always:
- Check if a pattern exists for your use case
- Inherit from appropriate base classes
- Use centralized services
- Follow established conventions
- Avoid creating duplicate functionality