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

1. Add the `PurrServices` component to a GameObject in your scene. It acts as a singleton and provides access to all services.

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