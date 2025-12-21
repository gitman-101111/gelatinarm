# Common Development Tasks Reference

> **Note**: This document provides implementation examples for common development tasks. For detailed architectural patterns, see [CODING_PATTERNS.md](CODING_PATTERNS.md).

## Adding a New Page

### 1. Create the View (XAML)
Example from `/Views/FavoritesPage.xaml`:
```xml
<views:BasePage
    x:Class="OurNamespace.Views.FavoritesPage"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:views="using:OurNamespace.Views">

    <Grid>
        <!-- Loading overlay -->
        <controls:LoadingOverlay x:Name="LoadingOverlay" />

        <!-- Main content -->
        <GridView x:Name="FavoritesGrid"
                  ItemsSource="{x:Bind ViewModel.FavoriteItems}"
                  SelectionMode="None"
                  IsItemClickEnabled="True"
                  ItemClick="OnItemClick">
            <!-- Item template here -->
        </GridView>
    </Grid>
</views:BasePage>
```

### 2. Create the Code-Behind
Example from `/Views/FavoritesPage.xaml.cs`:
```csharp
namespace OurNamespace.Views
{
    public sealed partial class FavoritesPage : BasePage
    {
        // Specify ViewModel type for automatic initialization
        protected override Type ViewModelType => typeof(FavoritesViewModel);

        // Typed property for easy access
        public FavoritesViewModel ViewModel => (FavoritesViewModel)base.ViewModel;

        public FavoritesPage() : base(typeof(FavoritesPage))
        {
            this.InitializeComponent();
        }

        // From SettingsPage - lifecycle override
        protected override async Task InitializePageAsync(object parameter)
        {
            // Set initial focus for controller navigation
            SetInitialFocus(ServerSettingsSection);

            // Load settings data
            await ViewModel.LoadSettingsAsync();
            await base.InitializePageAsync(parameter);
        }

        // Item click handler
        private void OnItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is BaseItemDto item)
            {
                NavigationService.NavigateToItemDetails(item);
            }
        }
    }
}
```

### 3. Create the ViewModel
Create `/ViewModels/NewFeatureViewModel.cs`:
```csharp
namespace OurNamespace.ViewModels
{
    public class NewFeatureViewModel : BaseViewModel
    {
        private readonly ISomeService _someService;
        
        public NewFeatureViewModel(ISomeService someService)
        {
            _someService = someService;
        }
        
        public async Task InitializeAsync(object parameter)
        {
            // Initialize with navigation parameter
            await LoadDataAsync();
        }
    }
}
```

### 4. Register the ViewModel
In `App.xaml.cs`, add to ConfigureServices:
```csharp
services.AddTransient<NewFeatureViewModel>();
```

### 5. Add Navigation
In `NavigationService.cs`:
- Add to `_pageMap` dictionary if needed
- Use built-in navigation helpers from BasePage

## Using BasePage Features

### Accessing Services
```csharp
// Built-in services available as properties
Logger?.LogInformation("Using Logger from BasePage");
NavigationService.Navigate(typeof(SomePage));
ErrorHandlingService.HandleError(ex);

// Get additional services
var apiClient = GetRequiredService<JellyfinApiClient>();
var optionalService = GetService<IOptionalService>(); // Can be null
```

### Navigation Helpers
```csharp
// Navigate to item details (handles all media types)
NavigationService.NavigateToItemDetails(mediaItem);

// Navigate to standard page
NavigationService.Navigate(typeof(SettingsPage));

// Get saved parameter for back navigation
var savedParam = GetSavedNavigationParameter();
```

### Fire and Forget Operations
```csharp
// Use the base helper (BasePage/BaseViewModel/BaseService)
FireAndForget(async () => await LoadDataAsync(), "LoadData");
```

## Adding a New Service

### 1. Define the Interface
In `/Services/ServiceInterfaces.cs`:
```csharp
public interface INewService
{
    Task<Result> PerformOperationAsync();
}
```

### 2. Implement the Service
Create `/Services/NewService.cs`:
```csharp
public class NewService : BaseService, INewService
{
    public NewService(ILogger<NewService> logger) : base(logger)
    {
    }

    public async Task<Result> PerformOperationAsync()
    {
        var context = CreateErrorContext("PerformOperation", ErrorCategory.System);
        try
        {
            // Implementation
            return new Result();
        }
        catch (Exception ex)
        {
            return await ErrorHandler.HandleErrorAsync<Result>(ex, context, null);
        }
    }
}
```

### 3. Register the Service
In `App.xaml.cs`:
```csharp
services.AddSingleton<INewService, NewService>();
// or
services.AddTransient<INewService, NewService>();
```

## Working with Media Items

### Navigate to Media Details
```csharp
// Use NavigationService directly
NavigationService.NavigateToItemDetails(item);
```

### Play Media
```csharp
// Navigate to media player - resume is handled automatically if UserData.PlaybackPositionTicks > 0
NavigationService.NavigateToItemDetails(item);

// Or for direct playback with custom parameters
var playbackParams = new MediaPlaybackParams
{
    ItemId = item.Id.ToString(),
    NavigationSourcePage = this.GetType()
};
NavigationService.Navigate(typeof(MediaPlayerPage), playbackParams);
```

Note: Resume functionality is handled automatically by the framework. When an item has `UserData.PlaybackPositionTicks > 0`, the play button will show "Resume (time)" and playback will start from the saved position.

## Handling User Input

### Button Click
```xml
<!-- XAML -->
<Button Content="Action" Click="OnActionClick" />

<!-- Code-behind -->
private void OnActionClick(object sender, RoutedEventArgs e)
{
    // Handle click
}
```

### Command Pattern
```xml
<!-- XAML -->
<Button Command="{x:Bind ViewModel.ActionCommand}" />

<!-- ViewModel -->
public ICommand ActionCommand { get; }

public MyViewModel()
{
    ActionCommand = new RelayCommand(ExecuteAction);
}

private async void ExecuteAction()
{
    // Handle command
}
```

## Data Loading Patterns

### Load on Navigation (BasePage Pattern)
```csharp
// In your page inheriting from BasePage
protected override async Task InitializePageAsync(object parameter)
{
    if (parameter is SomeType data)
    {
        await ViewModel.InitializeAsync(data);
    }
}
```

### Refresh Pattern (BaseViewModel Pattern)
```csharp
// In ViewModel inheriting from BaseViewModel
protected override async Task LoadDataCoreAsync(CancellationToken cancellationToken)
{
    var data = await _service.GetDataAsync(cancellationToken);
    await RunOnUIThreadAsync(() => Items.ReplaceAll(data));
}

// Usage
await ViewModel.RefreshAsync(); // Handles IsRefreshing state automatically
```

## Error Handling

### Using the Unified Error Handling Service

Both synchronous and asynchronous error handling are supported with simplified overloads:

#### Asynchronous Operations

##### In Services (inheriting from BaseService)
```csharp
// From UserProfileService.cs
public async Task<bool> LoadUserProfileAsync(CancellationToken cancellationToken)
{
    var context = CreateErrorContext("LoadUserProfile", ErrorCategory.User);
    try
    {
        _currentUser = await RetryAsync(
            async () => await _apiClient.Users.Me.GetAsync(null, cancellationToken)
                              .ConfigureAwait(false),
            Logger,
            cancellationToken: cancellationToken
        ).ConfigureAwait(false);

        if (_currentUser != null)
        {
            Logger.LogInformation($"User profile loaded: {_currentUser.Name}");
            return true;
        }
        return false;
    }
    catch (Exception ex)
    {
        return await ErrorHandler.HandleErrorAsync(ex, context, false, false);
    }
}

// Using HandleErrorWithDefaultAsync
public async Task<T> GetDataAsync<T>()
{
    try
    {
        return await _apiClient.GetAsync<T>();
    }
    catch (Exception ex)
    {
        return await HandleErrorWithDefaultAsync(ex, "GetData", default(T), ErrorCategory.Network, true);
    }
}
```

##### In ViewModels (inheriting from BaseViewModel)
```csharp
// Standard pattern with ErrorHandler
private async Task LoadDataAsync()
{
    var context = CreateErrorContext("LoadData", ErrorCategory.User);
    try
    {
        Data = await _service.GetDataAsync();
    }
    catch (Exception ex)
    {
        await ErrorHandler.HandleErrorAsync(ex, context, showUserMessage: true);
    }
}
```

##### In Controls (inheriting from BaseControl)
```csharp
private async void OnButtonClick(object sender, RoutedEventArgs e)
{
    try
    {
        await PerformActionAsync();
    }
    catch (Exception ex)
    {
        await HandleErrorAsync(ex, "ButtonClick", showUserMessage: true);
    }
}
```

#### Synchronous Operations

##### In Services (inheriting from BaseService)
```csharp
// Fire-and-forget error handling (synchronous context)
public void ProcessCachedData(string key)
{
    try
    {
        var data = _cache.Get(key);
        ProcessData(data);
    }
    catch (Exception ex)
    {
        // Fire-and-forget the error handling
        _ = HandleErrorAsync(ex, "ProcessCachedData", ErrorCategory.System);
    }
}
```

##### In ViewModels (inheriting from BaseViewModel)
```csharp
// Fire-and-forget error handling for UI events
private void UpdateDisplayName(string value)
{
    try
    {
        // Validation logic
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Display name cannot be empty");

        DisplayName = value;
        SavePreferences();
    }
    catch (Exception ex)
    {
        var context = CreateErrorContext("UpdateDisplayName", ErrorCategory.Validation);
        _ = ErrorHandler.HandleErrorAsync(ex, context, showUserMessage: true);
    }
}

// With return value
public string FormatDuration(TimeSpan duration)
{
    return ExecuteWithErrorHandling(
        () => _formattingService.Format(duration),
        defaultValue: "00:00"
    );
}
```

### DO NOT Use Manual Try-Catch
```csharp
// ❌ WRONG - Don't do this for async operations
public async Task<T> GetDataAsync<T>()
{
    try
    {
        return await _apiClient.GetAsync<T>();
    }
    catch (Exception ex)
    {
        Logger.LogError(ex, "Failed to get data");
        throw;
    }
}

// ✅ CORRECT - Use HandleErrorWithDefaultAsync
public async Task<T> GetDataAsync<T>()
{
    try
    {
        return await _apiClient.GetAsync<T>();
    }
    catch (Exception ex)
    {
        return await HandleErrorWithDefaultAsync(ex, "GetData", default(T), ErrorCategory.Network, true);
    }
}

// ❌ WRONG - Don't do this for sync operations
public string ProcessData(string input)
{
    try
    {
        return Transform(input);
    }
    catch (Exception ex)
    {
        Logger.LogError(ex, "Failed to process data");
        return string.Empty;
    }
}

// ✅ CORRECT - Use try/catch with fire-and-forget error handling
public string ProcessData(string input)
{
    try
    {
        return Transform(input);
    }
    catch (Exception ex)
    {
        _ = HandleErrorAsync(ex, "ProcessData", ErrorCategory.System);
        return string.Empty;
    }
}
```

## Dialog Service Usage

### Showing Dialogs

#### Information Messages
```csharp
// In ViewModel or Service
await _dialogService.ShowMessageAsync("Operation completed successfully", "Success");
```

#### Error Messages
```csharp
// ❌ WRONG - Don't show technical errors
await _dialogService.ShowErrorAsync($"SqlException: {ex.Message}");

// ✅ CORRECT - Show user-friendly messages
await _dialogService.ShowErrorAsync("Unable to load your media library. Please try again.");
```

#### Confirmation Dialogs
```csharp
var confirmed = await _dialogService.ShowConfirmationAsync(
    "Are you sure you want to delete this item?",
    "Confirm Delete",
    "Delete",
    "Cancel"
);

if (confirmed)
{
    await DeleteItemAsync();
}
```

### NEVER Create Dialogs Directly
```csharp
// ❌ WRONG - Don't do this
var dialog = new ContentDialog
{
    Title = "Error",
    Content = "Something went wrong",
    CloseButtonText = "OK"
};
await dialog.ShowAsync();

// ✅ CORRECT - Use IDialogService
await _dialogService.ShowErrorAsync("Something went wrong");
```

## UI Thread Execution

### In ViewModels (BaseViewModel)
```csharp
// Update UI properties from background thread
await RunOnUIThreadAsync(() => 
{
    IsLoading = false;
    StatusMessage = "Operation completed";
    Items.Clear();
    foreach (var item in newItems)
    {
        Items.Add(item);
    }
});
```

### In Services
```csharp
// Raise events on UI thread
await UIHelper.RunOnUIThreadAsync(() => 
{
    DataChanged?.Invoke(this, new DataChangedEventArgs(data));
}, dispatcher, Logger);

// Update UI from service callback
private async void OnDataReceived(object sender, DataEventArgs e)
{
    await UIHelper.RunOnUIThreadAsync(() => 
    {
        ProcessDataOnUI(e.Data);
    }, _dispatcher, Logger);
}
```

### In Controls/Views
```csharp
// Update UI elements
await UIHelper.RunOnUIThreadAsync(() => 
{
    LoadingOverlay.Visibility = Visibility.Collapsed;
    ContentGrid.Visibility = Visibility.Visible;
    StatusTextBlock.Text = "Ready";
}, Dispatcher, _logger);

// Focus management
await UIHelper.RunOnUIThreadAsync(() => 
{
    FirstButton?.Focus(FocusState.Programmatic);
}, Dispatcher, _logger);
```

### Fire-and-Forget Pattern (Use Sparingly)
```csharp
// For non-critical UI updates only
FireAndForget(() => UIHelper.RunOnUIThreadAsync(() =>
{
    // Update progress indicator
    ProgressBar.Value = progress;
}, Dispatcher, Logger));
```

### DO NOT Use Dispatcher Directly
```csharp
// ❌ WRONG - Don't do this
await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => 
{
    UpdateUI();
});

// ✅ CORRECT - Use UIHelper or RunOnUIThreadAsync
await UIHelper.RunOnUIThreadAsync(() => 
{
    UpdateUI();
}, Dispatcher, Logger);
```

## Working with Collections

### Bulk Updates to Observable Collections
```csharp
// Use the ReplaceAll extension method for better performance
Items.ReplaceAll(newItems); // Instead of Clear() + foreach Add()

// For incremental loading
public async Task LoadMoreItemsAsync()
{
    var nextPage = await _service.GetNextPageAsync();
    foreach (var item in nextPage)
    {
        Items.Add(item);
    }
}
```

## Caching Data

### Simple Cache
```csharp
private async Task<T> GetCachedDataAsync<T>(string key, Func<Task<T>> factory)
{
    var cached = _cacheManager.Get<T>(key);
    if (cached != null)
        return cached;
    
    var data = await factory();
    _cacheManager.Set(key, data, TimeSpan.FromMinutes(5));
    return data;
}
```

## XAML Binding Patterns

### x:Bind vs Binding
Always prefer x:Bind for performance (compile-time binding):

```xml
<!-- From SettingsPage.xaml - OneWay for display -->
<Run Text="{x:Bind TypedViewModel.ServerSettings.ConnectionTimeout, Mode=OneWay}" />

<!-- TwoWay for user input -->
<Slider Value="{x:Bind TypedViewModel.TextSize, Mode=TwoWay}"
        Minimum="12"
        Maximum="24" />

<!-- With FallbackValue -->
<Run Text="{x:Bind TypedViewModel.TextSize, Mode=OneWay, FallbackValue=14}" />

<!-- Boolean with converter -->
<Grid Visibility="{x:Bind IsLoading, Mode=OneWay,
      Converter={StaticResource BooleanToVisibilityConverter}}" />
```

### Common Converters Used
```xml
<!-- From Controls/LoadingOverlay.xaml -->
<TextBlock Text="{x:Bind LoadingText, Mode=OneWay}"
           Visibility="{x:Bind LoadingText, Mode=OneWay,
                       Converter={StaticResource NullableToVisibilityConverter}}" />

<!-- From views - ItemClick handling -->
<GridView ItemsSource="{x:Bind ViewModel.Items}"
          SelectionMode="None"
          IsItemClickEnabled="True"
          ItemClick="OnItemClick" />
```

## Xbox Controller Support

### Custom Focus Navigation
When you need to override the default XY focus navigation:
```xml
<Button x:Name="Button1"
        XYFocusDown="{x:Bind Button2}"
        XYFocusRight="{x:Bind Button3}" />
```

### Focus Capture Pattern
From LoadingOverlay.xaml - blocking navigation during loading:
```xml
<!-- Invisible button to capture focus and block navigation -->
<Button x:Name="FocusCapture"
        Opacity="0"
        IsTabStop="True"
        UseSystemFocusVisuals="False"
        HorizontalAlignment="Stretch"
        VerticalAlignment="Stretch"
        KeyDown="FocusCapture_KeyDown" />
```

Note: Controller button mappings and smart navigation features are handled automatically by the framework. See [CONTROLLER_ARCHITECTURE.md](CONTROLLER_ARCHITECTURE.md) for details.

## Debugging Tips

### Logging
```csharp
Logger?.LogInformation("Operation started");
Logger?.LogError(ex, "Operation failed");
```

### Debug Output
```csharp
#if DEBUG
System.Diagnostics.Debug.WriteLine($"Debug: {value}");
#endif
```

### Performance Monitoring
```csharp
var stopwatch = Stopwatch.StartNew();
// Operation
Logger?.LogInformation($"Operation took {stopwatch.ElapsedMilliseconds}ms");
```
