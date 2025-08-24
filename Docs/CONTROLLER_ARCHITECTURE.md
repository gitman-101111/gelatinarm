# Controller Input Architecture

## Overview

The Gelatinarm Xbox application uses a comprehensive controller input system designed specifically for Xbox gamepad navigation. The architecture consists of four main components that work together to provide seamless controller support throughout the application.

## Architecture Components

### 1. UnifiedDeviceService
**Location**: `/Services/UnifiedDeviceService.cs`  
**Interface**: `IUnifiedDeviceService`

#### Purpose
The UnifiedDeviceService is the core platform abstraction layer that:
- Detects Xbox environment and hardware capabilities
- Manages gamepad connections and state
- Provides device-specific feature detection (HDR, codecs, etc.)
- Handles system-level events (back button, controller connect/disconnect)

#### Key Responsibilities
- **Environment Detection**
  - Identifies Xbox vs non-Xbox platforms
  - Detects Xbox Series X/S vs Xbox One consoles
  - Determines hardware capabilities (HDR10, Dolby Vision, etc.)
  
- **Controller Management**
  - Monitors gamepad connections via `Gamepad.GamepadAdded/Removed` events
  - Tracks connected controller count
  - Provides current button state via `CurrentButtonState` property
  - Fires events for button presses and releases

- **System Integration**
  - Handles system back button requests
  - Manages full-screen mode transitions
  - Configures Xbox navigation mode
  - Monitors display and audio device changes

#### Key Properties
```csharp
bool IsXboxEnvironment          // True if running on Xbox
bool IsControllerConnected      // True if any gamepad connected
int ConnectedControllerCount    // Number of connected gamepads
GamepadButtons CurrentButtonState // Current button press state
```

#### Key Events
```csharp
event EventHandler<GamepadEventArgs> ControllerConnected
event EventHandler<GamepadEventArgs> ControllerDisconnected
event EventHandler<GamepadButtons> ButtonReleased
event EventHandler<GamepadButtonStateChangedEventArgs> ButtonStateChanged
event EventHandler<SystemEventArgs> SystemEvent
```

### 2. MediaControllerService
**Location**: `/Services/MediaControllerService.cs`  
**Interface**: `IMediaControllerService`

#### Purpose
The MediaControllerService specializes in media playback control via gamepad, translating controller buttons into media actions during video/audio playback.

#### Key Responsibilities
- **Button Mapping**
  - Maps controller buttons to media actions (play/pause, seek, etc.)
  - Provides customizable button mappings
  - Default mappings follow Xbox media player conventions

- **Playback Control (Plex-like scheme)**
  - A button: Play/Pause toggle
  - B button: Navigate back/Stop (always works)
  - Y button: Toggle playback statistics
  - D-pad Up/Down: Show/hide media controls
  - D-pad Left: Skip back 10 seconds
  - D-pad Right: Skip forward 30 seconds
  - Left Trigger: Skip back 10 minutes
  - Right Trigger: Skip forward 10 minutes
  - X button: Not mapped (matches Plex)
  - Shoulders: Not mapped (audio/subtitle via UI only)
  - Menu/View: Not mapped

- **Control Visibility Awareness**
  - Tracks when media controls are visible
  - Allows UI navigation when controls shown
  - Blocks media actions (except B button) during UI interaction

#### Key Methods
```csharp
Task<bool> HandleButtonPressAsync(ControllerButton button)
Task<bool> HandleKeyDownAsync(VirtualKey key)
void SetControlsVisible(bool visible)
void SetButtonMapping(Dictionary<ControllerButton, MediaAction> mapping)
```

#### Media Actions
```csharp
enum MediaAction
{
    PlayPause,
    NavigateBack,
    ShowStats,
    ShowInfo,
    Rewind,
    FastForward,
    PreviousAudioTrack,
    NextAudioTrack,
    ShowMenu,
    ToggleSubtitles
}
```

### 3. BasePage
**Location**: `/Views/BasePage.cs`

#### Purpose
BasePage provides controller support infrastructure for all application pages through lifecycle management and common functionality.

#### Key Responsibilities
- **Automatic Controller Setup**
  - Configures XY focus navigation via `ControllerInputHelper`
  - Handles system back button requests
  - Manages focus state for controller navigation

- **Back Navigation**
  - Subscribes to `SystemNavigationManager.BackRequested`
  - Provides virtual `HandleBackNavigation` for custom handling
  - Uses NavigationService for consistent navigation

- **Focus Management**
  - `SetInitialFocus(Control)` - Sets initial controller focus
  - Automatic focus on first focusable control if not specified
  - Virtual keyboard show/hide helpers

#### Controller-Related Methods
```csharp
protected void SetInitialFocus(Control control)
protected void ShowVirtualKeyboard()
protected void HideVirtualKeyboard()
protected virtual bool HandleBackNavigation(BackRequestedEventArgs e)
```

### 4. ControllerInputHelper
**Location**: `/Controls/ControllerInputHelper.cs`

#### Purpose
Static helper class providing utilities for configuring UI elements for optimal controller input experience.

#### Key Responsibilities
- **Page Configuration**
  - Enables XY focus navigation mode
  - Sets system focus visuals
  - Finds and focuses first focusable control
  - Recursively configures all child controls

- **Control Configuration**
  - TextBox: Sets input scope, disables spell check
  - PasswordBox: Optimizes for controller input
  - Button: Ensures focusability and tab stops
  - All controls: Enables XY focus navigation

- **Focus Management**
  - Programmatic focus with fallback strategies
  - First focusable control detection
  - Virtual keyboard show/hide utilities

#### Key Methods
```csharp
static void ConfigurePageForController(Page page, Control initialFocus, ILogger logger)
static void ConfigureTextBoxForController(TextBox textBox, InputScopeNameValue scope, ILogger logger)
static void SetInitialFocus(Control control, ILogger logger)
static Control FindFirstFocusableControl(DependencyObject container)
static void EnableXYFocusNavigation(FrameworkElement element, ILogger logger)
```

## Integration Flow

### 1. Application Startup
```
App.xaml.cs
├── Register UnifiedDeviceService as singleton
├── Service detects Xbox environment
└── Service starts monitoring gamepads
```

### 2. Page Navigation
```
NavigationService navigates to page
├── BasePage.OnNavigatedTo()
│   ├── Subscribe to BackRequested
│   └── Initialize page data
├── ControllerInputHelper.ConfigurePageForController()
│   ├── Enable XY focus navigation
│   ├── Configure all controls
│   └── Set initial focus
└── Page ready for controller input
```

### 3. Media Playback
```
MediaPlayerPage loaded
├── Create MediaControllerService
├── Subscribe to ActionTriggered events
├── Handle KeyDown events
│   ├── MediaControllerService.HandleKeyDownAsync()
│   ├── Map key to action
│   └── Fire ActionTriggered event
└── MediaPlayerPage handles action
```

### 4. Controller Input Flow
```
User presses gamepad button
├── Windows.Gaming.Input.Gamepad fires event
├── UnifiedDeviceService processes input
│   ├── Updates CurrentButtonState
│   └── Fires ButtonStateChanged event
├── Page KeyDown event (for media)
│   └── MediaControllerService translates to action
└── UI responds to input
```

## Best Practices

### 1. Page Implementation
```csharp
public sealed partial class MyPage : BasePage
{
    public MyPage() : base(typeof(MyPage))
    {
        InitializeComponent();
    }

    protected override async Task InitializePageAsync(object parameter)
    {
        // Set initial focus for controller
        SetInitialFocus(MyButton);
        
        // Load page data
        await base.InitializePageAsync(parameter);
    }
}
```

### 2. Custom Control Focus
```csharp
// In page Loaded event or InitializePageAsync
ControllerInputHelper.SetInitialFocus(MyGridView, Logger);

// Configure specific controls
ControllerInputHelper.ConfigureTextBoxForController(
    SearchBox, 
    InputScopeNameValue.Search, 
    Logger);
```

### 3. Media Controller Integration
```csharp
// In MediaPlayerPage
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
```

### 4. Control Visibility Management
```csharp
// When showing/hiding media controls
private void ShowMediaControls()
{
    MediaControlsPanel.Visibility = Visibility.Visible;
    _controllerService?.SetControlsVisible(true);
}

private void HideMediaControls()
{
    MediaControlsPanel.Visibility = Visibility.Collapsed;
    _controllerService?.SetControlsVisible(false);
}
```

## Controller Button Reference

### Standard Xbox Controller Mapping

| Button | Default Action | Media Player Action |
|--------|---------------|-------------------|
| A | Select/Activate | Play/Pause |
| B | Back/Cancel | Navigate Back |
| X | Context Action | Not mapped |
| Y | Menu/Options | Show Stats |
| D-pad Up/Down | Navigate | Show/Hide Controls |
| D-pad Left | Navigate | Skip Back 10 seconds |
| D-pad Right | Navigate | Skip Forward 30 seconds |
| Left Trigger | - | Skip Back 10 minutes |
| Right Trigger | - | Skip Forward 10 minutes |
| Right Trigger (Hold) | Jump to MusicPlayer | - |
| Left Shoulder | - | Not mapped |
| Right Shoulder | - | Not mapped |
| Menu | App Menu | Not mapped |
| View | View Options | Not mapped |
| Left Stick | Navigate | - |
| Right Stick | Scroll | - |

### Special Navigation Features

#### Right Trigger Hold - Quick MusicPlayer Access
**Implementation**: `RootContainer.xaml.cs`

When the Right Trigger is held for 500ms:
1. A timer starts on Right Trigger press
2. If held for 500ms, focus jumps directly to the MusicPlayer's play/pause button
3. If released before 500ms, normal Right Trigger action occurs (page scrolling)
4. This works from anywhere in the app when MusicPlayer is visible

This feature provides quick access to music controls from deep within navigation hierarchies without needing to navigate back through multiple screens.

## Troubleshooting

### Common Issues

1. **Focus Lost**
   - Ensure page has focusable controls
   - Check IsTabStop property on controls
   - Verify XYFocusKeyboardNavigation is enabled

2. **Navigation Not Working**
   - Confirm UnifiedDeviceService is registered
   - Check controller connection status
   - Verify page inherits from BasePage

3. **Media Controls Interfering**
   - Use SetControlsVisible to manage state
   - B button should always work for back
   - Check control visibility before processing

4. **Virtual Keyboard Issues**
   - Let Xbox system handle keyboard display
   - Don't force show/hide during focus events
   - Use proper InputScope on TextBox controls

## Architecture Diagram

```
┌─────────────────────────────────────────────────────────────┐
│                     Xbox Controller                          │
└────────────────────────┬────────────────────────────────────┘
                         │
                         ▼
┌─────────────────────────────────────────────────────────────┐
│                Windows.Gaming.Input API                      │
└────────────────────────┬────────────────────────────────────┘
                         │
                         ▼
┌─────────────────────────────────────────────────────────────┐
│              UnifiedDeviceService (Singleton)                │
│  • Gamepad connection monitoring                             │
│  • Button state tracking                                     │
│  • Platform capability detection                             │
│  • System event handling                                     │
└────────┬───────────────────────────────┬────────────────────┘
         │                               │
         ▼                               ▼
┌─────────────────────┐         ┌──────────────────────────┐
│      BasePage       │         │  MediaControllerService  │
│ • Lifecycle mgmt    │         │ • Button → Action mapping│
│ • Back navigation   │         │ • Playback control       │
│ • Focus helpers     │         │ • Control visibility     │
└──────────┬──────────┘         └────────────┬─────────────┘
           │                                  │
           ▼                                  ▼
┌─────────────────────┐         ┌──────────────────────────┐
│ ControllerInputHelper│         │    MediaPlayerPage      │
│ • Control config    │         │ • Video/Audio playback  │
│ • Focus management  │         │ • Controller integration │
│ • XY navigation     │         │ • UI control handling    │
└─────────────────────┘         └──────────────────────────┘
```

## Summary

The controller architecture provides a layered approach to Xbox gamepad input:

1. **UnifiedDeviceService** - Low-level platform and device management
2. **MediaControllerService** - Specialized media playback control
3. **BasePage** - Page-level controller support infrastructure
4. **ControllerInputHelper** - UI control configuration utilities

This separation of concerns allows for:
- Clean abstraction of platform-specific code
- Reusable controller support across all pages
- Specialized handling for media playback
- Consistent user experience throughout the app

The architecture ensures that Xbox users can navigate the entire application using only their gamepad, with intelligent focus management and context-aware button mappings.