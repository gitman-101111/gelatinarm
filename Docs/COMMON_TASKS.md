# Common Development Tasks Reference

> **Note**: This document provides implementation examples for common development tasks. For detailed architectural patterns, see [CODING_PATTERNS.md](CODING_PATTERNS.md).

## Adding a New Page

### 1. Create the View (XAML)
Create `/Views/NewFeaturePage.xaml`:
```xml
<views:BasePage
    x:Class="OurNamespace.Views.NewFeaturePage"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:views="using:OurNamespace.Views">
    
    <Grid>
        <!-- Your UI here -->
    </Grid>
</views:BasePage>
```

### 2. Create the Code-Behind
Create `/Views/NewFeaturePage.xaml.cs`:
```csharp
namespace OurNamespace.Views
{
    public sealed partial class NewFeaturePage : BasePage
    {
        // Specify ViewModel type for automatic initialization
        protected override Type ViewModelType => typeof(NewFeatureViewModel);
        
        // Typed property for easy access
        public NewFeatureViewModel ViewModel => (NewFeatureViewModel)base.ViewModel;
        
        public NewFeaturePage() : base(typeof(NewFeaturePage))
        {
            this.InitializeComponent();
        }
        
        // Override lifecycle methods as needed
        protected override async Task InitializePageAsync(object parameter)
        {
            // Set initial focus for controller
            SetInitialFocus(MyMainControl);
            
            // Initialize with navigation parameter
            await ViewModel.InitializeAsync(parameter);
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
// Use AsyncHelper.FireAndForget (per coding standards)
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
        return await ExecuteWithErrorHandlingAsync(
            async () =>
            {
                // Implementation
                return new Result();
            },
            context
        );
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
protected override async Task<LoadResult> LoadDataCoreAsync(
    bool forceRefresh, 
    CancellationToken cancellationToken)
{
    var data = await _service.GetDataAsync(cancellationToken);
    Items.ReplaceAll(data);
    return LoadResult.Success(data);
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
// Simple usage - auto-creates ErrorContext
public async Task<T> GetDataAsync<T>()
{
    return await ExecuteWithErrorHandlingAsync(
        async () => await _apiClient.GetAsync<T>(),
        CreateErrorContext("GetData", ErrorCategory.Network),
        showUserMessage: true
    );
}

// Using simplified overload (no ErrorContext needed)
public async Task<T> GetDataAsync<T>()
{
    var context = CreateErrorContext("GetData", ErrorCategory.Network);
    return await ExecuteWithErrorHandlingAsync(
        async () => await _apiClient.GetAsync<T>(),
        context,
        default(T),
        showUserMessage: true
    );
}
```

##### In ViewModels (inheriting from BaseViewModel)
```csharp
// Simple usage for void operations
private async Task LoadDataAsync()
{
    await ExecuteWithErrorHandlingAsync(
        async () =>
        {
            Data = await _service.GetDataAsync();
        },
        CreateErrorContext("LoadData", ErrorCategory.User),
        showUserMessage: true
    );
}
```

##### In Controls (inheriting from BaseControl)
```csharp
private async void OnButtonClick(object sender, RoutedEventArgs e)
{
    await ExecuteWithErrorHandlingAsync(
        async () =>
        {
            await PerformActionAsync();
        },
        "ButtonClick",
        showUserMessage: true
    );
}
```

#### Synchronous Operations

##### In Services (inheriting from BaseService)
```csharp
// Simple usage - auto-creates ErrorContext
public string GetCachedValue(string key)
{
    return ExecuteWithErrorHandling(
        () => _cache.Get(key),
        defaultValue: string.Empty
    );
}

// With custom category
public int CalculateValue(int input)
{
    return ExecuteWithErrorHandling(
        () => PerformComplexCalculation(input),
        defaultValue: 0,
        category: ErrorCategory.Validation
    );
}

// With custom ErrorContext
public bool ValidateData(object data)
{
    var context = CreateErrorContext("ValidateData", ErrorCategory.Validation);
    return ExecuteWithErrorHandling(
        () => PerformValidation(data),
        context,
        defaultValue: false,
        showUserMessage: true
    );
}
```

##### In ViewModels (inheriting from BaseViewModel)
```csharp
// Simple property setter with error handling
private void UpdateDisplayName(string value)
{
    ExecuteWithErrorHandling(() =>
    {
        // Validation logic
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Display name cannot be empty");
        
        DisplayName = value;
        SavePreferences();
    }, category: ErrorCategory.Validation, showUserMessage: true);
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

// ✅ CORRECT - Use ExecuteWithErrorHandlingAsync
public async Task<T> GetDataAsync<T>()
{
    var context = CreateErrorContext("GetData", ErrorCategory.Network);
    return await ExecuteWithErrorHandlingAsync(
        async () => await _apiClient.GetAsync<T>(),
        context,
        showUserMessage: true
    );
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

// ✅ CORRECT - Use ExecuteWithErrorHandling
public string ProcessData(string input)
{
    return ExecuteWithErrorHandling(
        () => Transform(input),
        defaultValue: string.Empty
    );
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
AsyncHelper.FireAndForget(UIHelper.RunOnUIThreadAsync(() => 
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

## Xbox Controller Support

### Custom Focus Navigation
When you need to override the default XY focus navigation:
```xml
<Button x:Name="Button1" 
        XYFocusDown="{x:Bind Button2}"
        XYFocusRight="{x:Bind Button3}" />
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

