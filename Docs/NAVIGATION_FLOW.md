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
         │ - Add server URL       │  │ - Home screen   │
         │ - Discover servers     │  │ - Quick access  │
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
                    │  │ [Search] [Favorites] [Library]  │ │
                    │  │ [Settings] [Now Playing]        │ │
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
Any Media Item
    ├── Movie → MovieDetailsPage → MediaPlayerPage
    ├── Episode → MediaPlayerPage (direct)
    ├── Series → SeasonDetailsPage → Episode → MediaPlayerPage
    ├── Album → AlbumDetailsPage → Track → MediaPlayerPage
    ├── Artist → ArtistDetailsPage → Album → Track
    ├── Person → PersonDetailsPage → Their Media → Details
    └── Collection → CollectionDetailsPage → Media Item → Details
```

### Back Navigation Rules
1. **MediaPlayerPage** → Returns to originating page
2. **Detail Pages** → Can navigate to other detail pages (maintains history)
3. **MainPage** → Cannot go back (root page)
4. **Login Flow** → Clears back stack after successful login

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

### Preserved on Navigation
- Scroll position in lists
- Current playback position
- Filter/sort selections
- Search history

### Reset on Navigation
- Loading states
- Error messages
- Temporary UI states

### Special States
- **PlaybackSession**: Maintained across navigation during playback
- **LibrarySelection**: Remembered for return navigation
- **Authentication**: Cleared on logout, forces return to login