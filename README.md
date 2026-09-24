# PurrServices

Unity client package for PurrNet's lobby and matchmaking backend services. Provides authentication, lobby management, real-time WebSocket connections, and chat functionality.

## Features

- **Authentication** - Device-based login, username/password registration and login, Steam sign-in
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

1. Open **Tools > PurrNet > PurrServices**. PurrServices initializes automatically at runtime, so no scene component is required. Sign in and create or link a project (**Create & Link**): every service request carries that project's key, and the server rejects requests without one.

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

// Or sign in with Steam (see "Steam sign-in" below)
await PurrSteamAuth.LoginAsync();
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

## Steam sign-in

Players can sign in with their Steam account. Steam verifies who they are, so the
player id is `steam:<steamid64>` and the display name is their Steam persona name.

**On the website** (your project → **Auth**):

1. Under **Steam**, enter your game's **App ID** and a **publisher Web API key**
   (Steamworks → Users & Permissions → Manage Groups → your group → Web API key).
   A personal key from steamcommunity.com/dev will not work, and neither will
   Valve's test app 480: Steam only validates tickets for apps the key's group
   owns. The key is stored encrypted and is never shown again.
2. Click **Check key with Steam** to confirm Steam accepts the key for that App ID.
3. Switch on the **Steam** sign-in provider.

Options: allow family-shared copies (default on; the player is the borrower),
refuse accounts you banned as publisher (default on), refuse VAC-banned accounts
(default off).

**In Unity with [Steamworks.NET](https://github.com/rlabrecque/Steamworks.NET)**
(installed through the Package Manager, `com.rlabrecque.steamworks.net`), the
`PurrServices.Steam` assembly compiles automatically:

```csharp
// Steam must be initialized and SteamAPI.RunCallbacks() pumped every frame
// (SteamManager or PurrNet's Steam transport do both).
var result = await PurrSteamAuth.LoginAsync();
if (!result.success)
    Debug.LogError(result.error);
```

If Steamworks.NET was imported as a `.unitypackage` instead, add the scripting
define `PURR_SERVICES_STEAMWORKS` to enable the assembly.

**With another Steam wrapper** (e.g. Facepunch.Steamworks), request a Web API
ticket for the identity `AuthService.SteamTicketIdentity` (`"purrnet"`) and pass
it hex encoded:

```csharp
var ticket = await Steamworks.SteamUser.GetAuthTicketForWebApiAsync(AuthService.SteamTicketIdentity);
var hex = BitConverter.ToString(ticket.Data).Replace("-", "");
await PurrServices.instance.auth.LoginWithSteamAsync(hex, Steamworks.SteamClient.Name);
ticket.Cancel();
```

Errors: `400` missing or malformed ticket, `401` Steam rejected the ticket, `403`
refused by your options (family sharing, bans), `502` Steam rejected the
project's Web API key, `503` Steam unreachable or Steam sign-in not configured.
