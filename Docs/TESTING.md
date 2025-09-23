# Testing Guide for Gelatinarm

This guide covers the manual testing procedures for Gelatinarm. The application requires manual testing on actual hardware due to the Xbox platform's unique requirements and UWP constraints.

## Table of Contents
1. [Testing Philosophy](#testing-philosophy)
2. [Development Environment Testing](#development-environment-testing)
3. [Xbox Testing](#xbox-testing)
4. [Test Scenarios](#test-scenarios)
5. [Performance Testing](#performance-testing)
6. [Known Limitations](#known-limitations)

## Testing Philosophy

Gelatinarm uses manual testing rather than automated unit tests because:
- UWP platform constraints make traditional unit testing challenging
- Xbox hardware behavior differs from Windows emulation
- Controller input requires physical testing
- Media playback involves real-time hardware interaction
- Visual Studio test suite does not accurately represent Xbox environment

## Development Environment Testing

### Windows Testing Setup

1. **Build Configuration**
   - Set configuration to `Debug` and platform to `x64`
   - Build solution (Ctrl+Shift+B)
   - Run on Local Machine (F5)

2. **Required Test Environment**
   - Jellyfin server instance (local or remote)
   - Test media library with various content types:
     - Movies (various codecs: H.264, H.265, etc.)
     - TV Shows (with multiple seasons/episodes)
     - Music (albums and individual tracks)
     - Mixed media collections

3. **Controller Testing on Windows**
   - Connect Xbox controller via USB or Bluetooth
   - Verify controller is recognized in Windows settings
   - Test all navigation paths with controller only

### Debug Tools

Use these Visual Studio tools during testing:
- **Output Window**: View debug logs and trace information
- **Memory Usage**: Monitor memory consumption (Tools → Diagnostic Tools)
- **Performance Profiler**: Identify performance bottlenecks
- **Live Visual Tree**: Inspect UI element hierarchy

## Xbox Testing

### Xbox Developer Mode Setup

1. **Enable Developer Mode**
   - Settings → System → Console Info → Developer Settings
   - Activate Developer Mode (requires developer account)
   - Restart Xbox when prompted

2. **Visual Studio Configuration**
   - Open Device Portal on Xbox (shows IP address)
   - In Visual Studio: Project → Properties → Debug
   - Set target to Remote Machine
   - Enter Xbox IP address
   - Authentication: Universal (Unencrypted)

3. **Deployment**
   - Build solution in Release mode for better performance
   - Deploy to Xbox (Build → Deploy Solution)
   - Launch from Visual Studio or Xbox Dev Home

### Xbox-Specific Testing

Test these Xbox-specific scenarios:
- **Controller Navigation**: All features accessible via gamepad
- **10-foot UI**: Text readable from typical viewing distance
- **Focus Management**: Clear focus indicators on all screens
- **System Integration**: Quick Resume, background audio
- **Memory Constraints**: Stay within 3GB limit (Xbox One)

## Test Scenarios

### Core Functionality

#### 1. Authentication Flow
- [ ] Manual server URL entry
- [ ] Username/password login
- [ ] Quick Connect authentication
- [ ] Token persistence across app restarts
- [ ] Logout and server switching

#### 2. Navigation Testing
- [ ] Home screen loads with content sections
- [ ] Library selection and browsing
- [ ] Search functionality (text input via virtual keyboard)
- [ ] Favorites page displays correctly
- [ ] Settings page all sections accessible
- [ ] Back button navigation throughout app
- [ ] Deep linking from notifications (if applicable)

#### 3. Media Browsing
- [ ] Browse movies library
- [ ] Browse TV shows (seasons and episodes)
- [ ] Browse music (artists, albums, tracks)
- [ ] Filter and sort options work
- [ ] Genre filtering
- [ ] Pagination/infinite scroll
- [ ] Thumbnail loading and caching

#### 4. Media Playback
- [ ] Start playback from beginning
- [ ] Resume from saved position
- [ ] Pause/play controls
- [ ] Seek forward/backward (10s, 30s, 10min)
- [ ] Audio track selection
- [ ] Subtitle track selection
- [ ] Playback quality adjustment
- [ ] Next episode auto-play
- [ ] Return to correct page after playback

#### 5. User Data Operations
- [ ] Mark items as favorite/unfavorite
- [ ] Mark items as watched/unwatched
- [ ] Playback progress saves correctly
- [ ] Continue watching section updates

### Controller Input Testing

Test all controller mappings documented in [CONTROLLER_ARCHITECTURE.md](CONTROLLER_ARCHITECTURE.md):

#### Navigation Mode
- [ ] D-pad navigates between UI elements
- [ ] A button selects/activates
- [ ] B button navigates back
- [ ] Left stick navigates (same as D-pad)
- [ ] Right stick scrolls lists
- [ ] Triggers for quick navigation (if implemented)

#### Media Player Mode
- [ ] A button: Play/Pause
- [ ] B button: Navigate back (always works)
- [ ] Y button: Toggle stats display
- [ ] D-pad Up/Down: Show/hide controls
- [ ] D-pad Left: Skip back 10 seconds
- [ ] D-pad Right: Skip forward 30 seconds
- [ ] Left Trigger: Skip back 10 minutes
- [ ] Right Trigger: Skip forward 10 minutes

### Edge Cases

#### Network Scenarios
- [ ] Server connection timeout handling
- [ ] Network disconnection during playback
- [ ] Token expiration and refresh
- [ ] Slow network performance
- [ ] Invalid server responses

#### Error Handling
- [ ] Invalid media format
- [ ] Corrupted media files
- [ ] Missing thumbnails/images
- [ ] Empty libraries
- [ ] Search with no results
- [ ] Server maintenance mode

#### State Management
- [ ] App suspension and resume
- [ ] Quick Resume from Xbox dashboard
- [ ] Background audio continues when minimized
- [ ] Navigation state preserved on back
- [ ] Settings persistence

## Performance Testing

### Memory Monitoring

1. **Xbox One Constraints** (3GB limit)
   - Monitor memory usage in Device Portal
   - Test with large libraries (1000+ items)
   - Verify image caching doesn't exceed limits
   - Check for memory leaks during extended use

2. **Memory Profiling Points**
   - After app launch: Baseline memory
   - After browsing large library: Image cache impact
   - During video playback: Media buffer allocation
   - After 30 minutes use: Leak detection

### Performance Metrics

Track these key metrics:
- **Page Load Time**: Should be < 2 seconds
- **Scroll Performance**: Maintain 60 FPS
- **Search Response**: Results within 1-2 seconds
- **Playback Start**: Video begins within 5 seconds
- **Image Loading**: Thumbnails appear progressively

### Load Testing

Test with realistic data volumes:
- Libraries with 500+ movies
- TV shows with 10+ seasons
- Music libraries with 1000+ tracks
- Continue watching with 20+ items
- Search across entire library

## Known Limitations

### Platform Constraints
- Cannot test certain Xbox features on Windows (HDR, Dolby Vision)
- Virtual keyboard behavior differs between Windows and Xbox
- Some codec support varies by platform
- Quick Resume behavior only testable on Xbox

### Testing Gaps
- Automated regression testing not feasible
- Cannot simulate all network conditions
- Multi-user scenarios limited by single Xbox
- Long-term stability requires extended manual testing

## Test Checklist Template

Use this template for release testing:

```markdown
## Release Test Checklist

**Version**: X.X.X
**Date**: YYYY-MM-DD
**Tester**: Name

### Environment
- [ ] Windows 10/11
- [ ] Xbox One
- [ ] Xbox Series S/X
- [ ] Controller tested
- [ ] Keyboard/Mouse tested

### Core Features
- [ ] Authentication works
- [ ] Navigation complete
- [ ] Media playback functional
- [ ] Settings persist
- [ ] Error handling graceful

### Performance
- [ ] Memory within limits
- [ ] Acceptable load times
- [ ] Smooth scrolling
- [ ] No crashes/hangs

### Notes
[Any issues or observations]
```

## Reporting Issues

When reporting test failures:

1. **Environment Details**
   - Platform (Windows/Xbox model)
   - Build configuration
   - Network conditions

2. **Reproduction Steps**
   - Exact navigation path
   - Input method (controller/keyboard)
   - Media type being tested

3. **Expected vs Actual**
   - What should happen
   - What actually happened
   - Any error messages

4. **Supporting Information**
   - Screenshots/video if UI issue
   - Debug output if available
   - Memory usage if performance issue