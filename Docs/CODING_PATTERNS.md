# Gelatinarm Coding Patterns

This document describes the established coding patterns in the Gelatinarm codebase. These patterns ensure consistency, reduce duplication, and improve maintainability.

## Table of Contents
1. [Error Handling Pattern](#error-handling-pattern)
2. [Navigation Pattern](#navigation-pattern)
3. [User Data Operations Pattern](#user-data-operations-pattern)
4. [Data Loading Pattern](#data-loading-pattern)
5. [Settings Management Pattern](#settings-management-pattern)
6. [Page Lifecycle Pattern](#page-lifecycle-pattern)
7. [Service Architecture Pattern](#service-architecture-pattern)
8. [Controller Input Pattern](#controller-input-pattern)
9. [Commit and Pull Request Guidelines](#commit-and-pull-request-guidelines)
10. [Temporary Workarounds](#temporary-workarounds)

## Error Handling Pattern

### Overview
All error handling is centralized through the `ErrorHandlingService` to provide consistent error messages, logging, and user feedback.

### Implementation

#### In Services (inheriting from BaseService)
```csharp
// Method 1: Using try/catch with ErrorHandler.HandleErrorAsync
public async Task<UserDto> GetUserAsync(string userId)
{
    var context = CreateErrorContext("GetUser", ErrorCategory.User);
    try
    {
        return await _apiClient.Users[userId].GetAsync();
    }
    catch (Exception ex)
    {
        // ErrorHandler.HandleErrorAsync logs error and returns default value
        return await ErrorHandler.HandleErrorAsync<UserDto>(ex, context, null, showUserMessage: false);
    }
}

// Method 2: Using RetryAsync with automatic retry and error handling
public async Task<UserDto> LoadUserProfileAsync(CancellationToken cancellationToken)
{
    // RetryAsync automatically handles retries with exponential backoff
    return await RetryAsync(
        async () => await _apiClient.Users.Me.GetAsync(null, cancellationToken),
        cancellationToken: cancellationToken
    ).ConfigureAwait(false);
}
```

#### In ViewModels (inheriting from BaseViewModel)
```csharp
// Standard pattern using ErrorHandler directly
private async Task LoadDataAsync()
{
    var context = CreateErrorContext("LoadData", ErrorCategory.User);
    try
    {
        var data = await _service.GetDataAsync();
        await RunOnUIThreadAsync(() =>
        {
            Items.ReplaceAll(data);
        });
    }
    catch (Exception ex)
    {
        // ErrorHandler is a protected property from BaseViewModel
        await ErrorHandler.HandleErrorAsync(ex, context, showUserMessage: true);
    }
}

// Using the built-in LoadDataCoreAsync pattern
protected override async Task LoadDataCoreAsync(CancellationToken cancellationToken)
{
    // BaseViewModel automatically handles errors when you override this method
    var data = await _service.GetDataAsync(cancellationToken);
    await RunOnUIThreadAsync(() => Items.ReplaceAll(data));
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
    protected override async Task LoadDataCoreAsync(CancellationToken cancellationToken)
    {
        // Load fresh data
        var items = await _mediaService.GetLibraryItemsAsync(cancellationToken);

        // Update UI on UI thread
        await RunOnUIThreadAsync(() =>
        {
            Items.Clear();
            foreach (var item in items)
            {
                Items.Add(item);
            }
        });
    }
}
```

#### Refresh Pattern
```csharp
protected override async Task RefreshDataCoreAsync()
{
    // Custom refresh logic if different from load
    // Otherwise just reload all data
    _loadDataCts?.Cancel();
    _loadDataCts = new CancellationTokenSource();
    await LoadDataCoreAsync(_loadDataCts.Token);
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
Settings ViewModels inherit from `BaseViewModel` and use PreferencesService for consistent load/save operations.

### Implementation

```csharp
public class ServerSettingsViewModel : BaseViewModel
{
    private readonly IPreferencesService _preferencesService;
    private string _serverUrl;

    public string ServerUrl
    {
        get => _serverUrl;
        set => SetProperty(ref _serverUrl, value);
    }

    protected override async Task LoadDataCoreAsync(CancellationToken cancellationToken)
    {
        ServerUrl = await _preferencesService.GetAsync<string>(
            PreferenceConstants.JELLYFIN_SERVER_URL,
            string.Empty);
    }

    public async Task SaveSettingsAsync()
    {
        await _preferencesService.SetAsync(
            PreferenceConstants.JELLYFIN_SERVER_URL,
            ServerUrl);
    }
}
```

### Features
- Settings loaded through LoadDataCoreAsync pattern
- Direct access to PreferencesService
- Manual save operations when needed

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
    protected async Task HandleErrorAsync(Exception ex, string operation, ErrorCategory category = ErrorCategory.System);
    protected async Task<T> HandleErrorWithDefaultAsync<T>(Exception ex, string operation, T defaultValue = default);
    protected async Task<T> RetryAsync<T>(Func<Task<T>> operation, int maxRetries = 3);
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

## Commit and Pull Request Guidelines

### Branch Naming
Use descriptive branch names that indicate the type of change:
```
feature/add-subtitle-selection
fix/playback-resume-issue
refactor/service-layer
docs/update-api-reference
```

### Commit Message Format
Follow the conventional commit format for consistency:
```
<type>: <description>

[optional body]

[optional footer]
```

#### Commit Types
- `feat`: New feature
- `fix`: Bug fix
- `refactor`: Code refactoring (no functional changes)
- `docs`: Documentation changes
- `style`: Code style changes (formatting, missing semicolons, etc.)
- `perf`: Performance improvements
- `test`: Test additions or changes
- `chore`: Maintenance tasks, dependency updates

#### Examples
```
feat: Add subtitle track selection in media player

- Added SubtitleService for track management
- Updated MediaPlayerPage with subtitle menu
- Integrated with existing playback controls

fix: Resolve playback resume position on HLS streams

Resume position was being ignored for HLS content.
Applied workaround to detect manifest offset and adjust
seek position accordingly.

docs: Update API reference for MediaPlaybackService
```

### Pull Request Process

#### Before Creating a PR
1. **Test thoroughly**
   - Build and run on Windows
   - Test with Xbox controller
   - Deploy to Xbox if possible
   - Verify memory usage stays within limits (3GB on Xbox One)

2. **Code quality checks**
   - Code compiles without warnings
   - Follows patterns documented in this guide
   - Includes proper error handling
   - No hardcoded values that should be configurable

3. **Update documentation** if needed
   - Update relevant .md files for new features
   - Add XML comments for new public APIs
   - Document any workarounds with clear comments

#### PR Description Template
```markdown
## Description
Brief description of what this PR does

## Type of Change
- [ ] Bug fix
- [ ] New feature
- [ ] Refactoring
- [ ] Documentation

## Testing
How to test these changes:
1. Step one
2. Step two
3. Expected result

## Checklist
- [ ] Code follows project patterns
- [ ] Tested on Windows
- [ ] Tested with controller
- [ ] Documentation updated if needed
- [ ] Memory efficient for Xbox
```

### Code Review Considerations
- **Functionality**: Does it work as intended?
- **Patterns**: Does it follow existing patterns documented here?
- **Performance**: Is it optimized for Xbox hardware constraints?
- **Error Handling**: Are errors handled properly using the centralized service?
- **Controller Support**: Does it work with gamepad navigation?
- **Memory Usage**: Does it respect Xbox memory limits?

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

These patterns provide:

1. **Reduced code duplication**
2. **Improved consistency** across the codebase
3. **Simplified maintenance** through centralization
4. **Enhanced testability** with clear separation
5. **Standardized error handling** and user feedback
6. **Automatic controller support**

When implementing new features:
- Check if a pattern exists for your use case
- Inherit from appropriate base classes
- Use centralized services
- Follow established conventions
- Avoid creating duplicate functionality