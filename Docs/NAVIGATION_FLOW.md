# Navigation Flow Diagram

## Visual Navigation Map

```
┌─────────────────────────────────────────────────────────────────────┐
│                          APP LAUNCH                                  │
└─────────────────────┬───────────────────────┬───────────────────────┘
                      │                       │
              [No Server/Token]          [Valid Token]
                      │                       │
                      ▼                       ▼
         ┌────────────────────────┐  ┌─────────────────┐
         │ ServerSelectionPage    │  │    MainPage     │
         │ - Enter server URL     │  │ - Home screen   │
         │ - Connect to server    │  │ - Quick access  │
         └───────────┬────────────┘  └────────┬────────┘
                     │                         │
                     ▼                         │
         ┌────────────────────────┐           │
         │     LoginPage          │           │
         │ - Username/Password    │           │
         │ - Quick Connect        │           │
         └───────┬──────┬─────────┘           │
                 │      │                      │
        [Login]  │      │ [Quick Connect]     │
                 │      ▼                      │
                 │  ┌─────────────────────┐   │
                 │  │QuickConnectInstructions│ │
                 │  │ - Show code          │   │
                 │  │ - Wait for approval │   │
                 │  └──────────┬──────────┘   │
                 │             │               │
                 └─────────────┴───────────────┘
                               │
                               ▼
                    ┌──────────────────────────────────────┐
                    │            MAIN PAGE                  │
                    │                                       │
                    │  ┌─────────────────────────────────┐ │
                    │  │     Command Bar Actions         │ │
                    │  │ [Refresh] [Search] [Library]    │ │
                    │  │ [Favorites] [Settings]          │ │
                    │  └────┬──────┬─────────┬───────────┘ │
                    │       │      │         │             │
                    │       ▼      ▼         ▼             │
                    │   SearchPage FavoritesPage LibrarySelectionPage
                    │                                      │
                    │  ┌─────────────────────────────────┐ │
                    │  │     Content Sections            │ │
                    │  │ - Continue Watching             │ │
                    │  │ - Latest Movies                 │ │
                    │  │ - Latest TV Shows               │ │
                    │  │ - Recently Added                │ │
                    │  └────────────┬────────────────────┘ │
                    └───────────────┼──────────────────────┘
                                    │
                          [Click any item]
                                    │
                    ┌───────────────┴───────────────────┐
                    ▼                                   ▼
        ┌─────────────────────┐           ┌─────────────────────┐
        │   Movie/Episode     │           │    TV Series        │
        │ MovieDetailsPage    │           │ SeasonDetailsPage   │
        │ - Play/Resume       │           │ - Episode list      │
        │ - Similar items     │           │ - Season selection  │
        └──────────┬──────────┘           └──────────┬──────────┘
                   │                                  │
                   └──────────────┬───────────────────┘
                                  ▼
                       ┌─────────────────────┐
                       │  MediaPlayerPage    │
                       │ - Video playback    │
                       │ - Playback controls │
                       │ - Next episode      │
                       └─────────────────────┘
```

## Navigation Paths

### Primary Navigation (from MainPage)
- **Search** → SearchPage → Item Details → MediaPlayer
- **Favorites** → FavoritesPage → Item Details → MediaPlayer  
- **Library** → LibrarySelectionPage → LibraryPage → Item Details
- **Settings** → SettingsPage (with sub-pages for different settings)

### Content Navigation
```
Any Media Item (via NavigateToItemDetails)
    ├── Movie → MovieDetailsPage → MediaPlayerPage
    ├── Episode → SeasonDetailsPage (shows episode in season context)
    ├── Series → SeasonDetailsPage → Episode → MediaPlayerPage
    ├── Season → SeasonDetailsPage → Episode → MediaPlayerPage
    ├── Audio (Song) → MusicPlayerService.PlayItem() (no navigation)
    ├── Album → AlbumDetailsPage → Track → MusicPlayer
    ├── Artist → ArtistDetailsPage → Album → Track
    ├── Person → PersonDetailsPage → Their Media → Details
    └── BoxSet → CollectionDetailsPage → Media Item → Details
```

### Back Navigation Rules
1. **MediaPlayerPage** → Returns to originating page (back stack cleaned to prevent buildup)
2. **Detail Pages** → Can navigate to other detail pages (maintains history)
3. **Episode-to-Episode** → Special handling to prevent MediaPlayerPage stack buildup
4. **Circular Navigation** → History cleared when detected to prevent loops
5. **Stack Depth Limit** → History cleared when exceeds MAX_BACK_STACK_DEPTH * 2

### Special Navigation Cases

#### Library Flow
```
LibrarySelectionPage
    └── LibraryPage (with selected library)
        ├── Filter/Sort options
        ├── Genre selection
        └── Media items → Detail pages
```

#### Settings Flow
```
SettingsPage (single page with sections)
    ├── Server Settings (sign out, connection)
    ├── Playback Settings (quality preferences)
    └── Network Settings (bandwidth, timeouts)
```

#### Cross-Navigation
- Person in movie → PersonDetailsPage → Other movies with person
- Genre tag → LibraryPage (filtered by genre)
- Artist in album → ArtistDetailsPage → Other albums
- Similar items → Same type detail page

## Navigation State Management

### Preserved on Navigation (for cached pages)
- Page state for NavigationCacheMode.Enabled pages
- Current playback position (managed by services)
- Filter/sort selections (stored in ViewModels)

### Reset on Navigation
- Loading states
- Error messages
- Temporary UI states
- Pages with NavigationCacheMode.Disabled get fresh instances

### Special States
- **PlaybackSession**: Maintained by MediaPlaybackService across navigation
- **Navigation Stack**: Cleaned when MediaPlayerPage accumulates
- **Authentication**: On logout, navigates to ServerSelectionPage