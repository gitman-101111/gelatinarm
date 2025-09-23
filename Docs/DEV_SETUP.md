# Development Environment Setup

## Prerequisites
- Windows 10/11 with Visual Studio 2022
- Xbox Developer Mode enabled (for testing on console)
- .NET SDK compatible with UWP development
- Jellyfin server instance for testing

## Getting Started

### 1. Clone and Open Project
```bash
git clone [repository-url]
cd gelatinarm
```
Open `Gelatinarm.sln` in Visual Studio 2022

### 2. First Build
1. Set build configuration to `Debug` and platform to `x64`
2. Build solution (Ctrl+Shift+B)
3. NuGet packages will restore automatically

### 3. Running the Application

#### On Windows (Development)
1. Set `Gelatinarm` as startup project
2. Select "Local Machine" as target
3. Press F5 to run with debugging

#### On Xbox
1. Enable Developer Mode on Xbox
2. Pair Visual Studio with Xbox
3. Select "Remote Machine" as target
4. Enter Xbox IP address
5. Deploy and run

### 4. Testing Workflow
1. **Server Connection**: Enter your Jellyfin server URL
2. **Login**: Use existing Jellyfin credentials
3. **Browse**: Navigate content using keyboard or Xbox controller
4. **Play**: Select any media to test playback

## Common Development Tasks

### Adding a New Feature
1. Identify where it belongs (View, Service, or Control)
2. Follow existing patterns in similar files
3. Register new services in App.xaml.cs
4. Update navigation if adding new pages

### Debugging Network Issues
1. Check `AuthenticationService` for login problems
2. Enable network logging in `App.xaml.cs`
3. Use Fiddler to inspect HTTP traffic
4. Check server logs for errors

### Testing Xbox Features
- Always test with controller
- Verify focus navigation works
- Check memory usage on Xbox One (3GB limit)
- Test suspend/resume scenarios

### Modifying UI
- XAML files in `/Views` and `/Controls`
- Use existing styles from App.xaml
- Test on different screen sizes
- Ensure gamepad navigation works

## Project Conventions

### Naming
- Views: `[Feature]Page.xaml`
- ViewModels: `[Feature]ViewModel.cs`
- Services: `[Function]Service.cs`
- Interfaces: `I[Name].cs`

### Code Organization
- One class per file
- Interfaces defined with their implementations or in ServiceInterfaces.cs for shared interfaces
- Constants in appropriate Constants file
- Helpers for reusable logic

### Async Patterns
```csharp
// In ViewModels
await LoadDataAsync();

// In Services  
return await SomeOperation().ConfigureAwait(false);
```

### Error Handling
Use the unified error handling pattern:
```csharp
// In Services
var context = CreateErrorContext("OperationName", ErrorCategory.Network);
try
{
    return await PerformOperationAsync();
}
catch (Exception ex)
{
    return await ErrorHandler.HandleErrorAsync<T>(ex, context, defaultValue, showUserMessage: true);
}

// In ViewModels - override LoadDataCoreAsync for automatic error handling
protected override async Task LoadDataCoreAsync(CancellationToken cancellationToken)
{
    // Errors are automatically handled by BaseViewModel
    var data = await _service.GetDataAsync(cancellationToken);
    await RunOnUIThreadAsync(() => Items.ReplaceAll(data));
}
```

## Troubleshooting

### Build Errors
- Clean solution and rebuild
- Check NuGet package restoration
- Verify Windows SDK version
- Ensure UWP workload is installed in Visual Studio

### Service Initialization Failures
Common warning: "Failed to get [Service] - continuing without it"
- Non-critical services can fail without stopping the app
- Check App.xaml.cs InitializeCoreServices for initialization order
- Critical services that fail will prevent app startup

### Runtime Crashes
- Check debug output window for initialization errors
- Verify all required services are registered in ConfigureServices
- Check for null reference exceptions in ViewModels
- Ensure ViewModels are registered as Transient or Singleton appropriately

### Xbox Deployment Issues
- Ensure Developer Mode is active
- Check network connectivity between PC and Xbox
- Verify Visual Studio pairing (may need to re-pair)
- Check Xbox IP address hasn't changed
- Ensure both devices are on same network

### Authentication Problems
- Check server URL format (include protocol: https://...)
- Verify server is accessible from Xbox/PC
- Token stored in Windows Credential Vault - may need to clear
- Check AuthenticationService logs for specific errors
- Verify Jellyfin server version compatibility

### Navigation Issues
- Pages must inherit from BasePage for proper initialization
- Check NavigationService.NavigateToItemDetails for unsupported media types
- Verify navigation parameter passing in InitializePageAsync
- Check for navigation loops in back stack

## Key Files to Understand First
1. `App.xaml.cs` - Application setup
2. `MainPage.xaml/cs` - Home screen
3. `BaseViewModel.cs` - ViewModel pattern
4. `NavigationService.cs` - Navigation
5. `AuthenticationService.cs` - Server communication

## Useful Resources
- Jellyfin API documentation
- UWP development guides
- Xbox development documentation
- MVVM pattern references