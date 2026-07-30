# PurrServices

Unity client package for PurrNet's lobby and matchmaking backend services. Provides authentication, lobby management, real-time WebSocket connections, and chat functionality.

## Features

- **Authentication** - Device-based login, username/password registration and login
- **Lobby Management** - Create, join (by ID or code), leave, destroy, and list lobbies
- **Real-time Connection** - WebSocket-based lobby connections with automatic reconnection and exponential backoff
- **Player Management** - Kick players, manage metadata, track lobby state
- **Chat** - Send and receive chat messages within lobbies
- **Debug GUI** - Built-in debug UI for testing all services at runtime

## Installation

### Unity Package Manager (UPM)

Open the Unity Package Manager (`Window > Package Manager`), click the `+` button, and select **Add package from git URL...**

**Release (recommended):**
```
https://github.com/PurrNet/PurrServices.git?path=Assets/Tool#release
```

**Development branch:**
```
https://github.com/PurrNet/PurrServices.git?path=Assets/Tool#dev
```

## Quick Start

1. Open **Tools > PurrNet > PurrServices**. PurrServices initializes automatically at runtime, so no scene component is required. With no project linked, lobby and device authentication use the free development tier automatically, namespaced by Unity's `Application.identifier`. This tier is not intended for production; create and link a project before releasing your game.

   The selected project key and service URL are stored through PurrNet's `ApplicationConstants` and compiled into player builds.

   Player builds and the Unity Editor have separate profiles. Each app remembers its own environment scope within each profile, defaulting to `production` for builds and `editor` in the Unity Editor. The Editor can instead use the build profile or a different linked project.

   Lobby compatibility is derived from `Application.version` and enforced by listing, quick join, direct join, join codes, polling, and WebSocket connections.

2. **Authenticate:**
```csharp
var purr = PurrServices.instance;

// Device-based login (anonymous)
await purr.auth.LoginAsync(SystemInfo.deviceUniqueIdentifier);

// Or register with username/password
await purr.auth.RegisterAsync("username", "password");

// Or login with username/password
await purr.auth.LoginWithPasswordAsync("username", "password");
```

3. **Create and join lobbies:**
```csharp
// Create a lobby
var result = await purr.lobbies.CreateAsync(new CreateLobbyOptions
{
    name = "My Lobby",
    maxPlayers = 4
});

// List available lobbies
var list = await purr.lobbies.ListAsync();

// Join by code
var joined = await purr.lobbies.JoinByCodeAsync("ABC123");
```

4. **Connect via WebSocket for real-time updates:**
```csharp
var connection = purr.lobbies.Connect(lobbyId, playerToken);
```
