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
- **ViewModels**: C# classes with INotifyPropertyChanged
  - Inherit from `BaseViewModel` for common functionality
  - Automatically initialized by BasePage when `ViewModelType` is specified
- **Data Binding**: x:Bind for compile-time binding
- **Commands**: ICommand implementation for user actions

### 2. Dependency Injection
- **Container**: Built in App.xaml.cs
- **Lifetime Management**: 
  - Singleton: Shared services (Navigation, Authentication)
  - Transient: ViewModels, page-specific services
- **Constructor Injection**: Services injected via constructors

### 3. Service Layer Pattern
- **Interfaces**: Define contracts in ServiceInterfaces.cs
- **Implementations**: Concrete classes in Services folder
- **Separation of Concerns**: Each service has single responsibility

### 4. Repository Pattern
- **Data Access**: Services abstract data source
- **Caching Layer**: In-memory cache before server calls
- **Consistent Interface**: Same methods regardless of source

### 5. Observer Pattern
- **Events**: Services raise events for state changes
- **INotifyPropertyChanged**: ViewModels notify Views
- **Weak References**: Prevent memory leaks

### 6. Error Handling Pattern
- **ErrorHandlingService**: Centralized error processing
- **ErrorContext**: Contextual information about errors
- **Category-based handling**: Different strategies for different error types
- **User-friendly messages**: Automatic conversion of technical errors

## Key Architectural Decisions

### Single Page Instance
- Pages use `NavigationCacheMode="Enabled"`
- State preserved during navigation
- Reduces memory allocation

### Service Locator Anti-pattern Avoidance
- All dependencies injected
- No static service references
- Testable architecture

### Async/Await Throughout
- All I/O operations async
- ConfigureAwait(false) in services
- UI thread protection

### Error Handling Strategy
```
View (User Message) ← ViewModel (Handle) ← Service (Throw) ← Network (Error)
```

### Memory Management
- Explicit disposal of resources
- Weak event handlers
- Cache size limits

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
- Large collections use incremental loading
- Images load on-demand

### Data Caching
- Memory cache with expiration
- Size-based eviction
- Separate caches by data type

### Network Optimization
- Retry with exponential backoff
- Request batching where possible
- Image size optimization

### Memory Management
- Dispose pattern for resources
- Weak references for events
- Regular cache cleanup

### Input Handling
- Event-based gamepad input (no polling)
- MediaControllerService handles all media playback input
- XY focus navigation disabled during playback to prevent analog stick sounds
- Control visibility state determines input behavior

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
- **BaseService**: All services inherit error handling via `ExecuteWithErrorHandlingAsync` and `ExecuteWithErrorHandling`
- **BaseViewModel**: All ViewModels inherit error handling capabilities
- **BaseControl**: All controls inherit standardized error handling

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

**Asynchronous Operations:**
```csharp
// Simple usage - auto-creates ErrorContext
await ExecuteWithErrorHandlingAsync(async () => 
{
    await SomeAsyncOperation();
}, showUserMessage: true);

// With return value and custom category
var result = await ExecuteWithErrorHandlingAsync(
    async () => await GetDataAsync(),
    CreateErrorContext("GetData", ErrorCategory.Network),
    defaultValue: new List<Item>()
);
```

**Synchronous Operations:**
```csharp
// Simple usage - auto-creates ErrorContext
ExecuteWithErrorHandling(() => 
{
    SomeSyncOperation();
});

// With return value, default, and category
var result = ExecuteWithErrorHandling(
    () => GetSyncData(),
    defaultValue: "default",
    category: ErrorCategory.System
);
```

#### Simplified Overloads

The error handling methods provide simplified overloads that automatically create ErrorContext:
- Method name is captured automatically via CallerMemberName
- ErrorCategory defaults to System but can be specified
- Custom ErrorContext can still be provided for complex scenarios

#### Best Practices
1. Use the simplified overloads for most cases - avoid creating ErrorContext variables
2. Always use ErrorCategory to classify errors appropriately  
3. Let ErrorHandlingService generate user messages
4. Use showUserMessage parameter wisely (true for user-initiated actions)
5. Don't catch exceptions just to log them - use the service
6. Maintain error context through the call stack
7. NEVER show technical error messages directly to users
8. Use synchronous ExecuteWithErrorHandling for synchronous operations instead of try-catch

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
   AsyncHelper.FireAndForget(UIHelper.RunOnUIThreadAsync(() => 
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