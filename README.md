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

Players can sign in with their Steam account; the player id is `steam:<steamid64>`.
On the website (your project → **Auth** → **Steam**) pick how much to trust the game:

- **Verify with Steam** (recommended). Steam vouches for the player, and the name is
  their Steam persona name. Enter your game's **App ID** and a **publisher Web API
  key** (Steamworks → Users & Permissions → Manage Groups → your group → Web API
  key). A personal key from steamcommunity.com/dev will not work, and neither will
  Valve's test app 480: Steam only validates tickets for apps the key's group owns.
  The key is stored encrypted and never shown again; **Check key with Steam**
  confirms Steam accepts it for that App ID. Options: allow family-shared copies
  (default on; the player is the borrower), refuse accounts you banned as publisher
  (default on), refuse VAC-banned accounts (default off).
- **Trust the game.** No setup: the Steam ID and name the game reports are taken
  as they are. Anyone with a modified client can sign in as any Steam account, so
  use it while starting out, or when identity does not matter for your game.
  Player ids stay the same when you switch to verifying later.

Then switch on the **Steam** sign-in provider. The game code is the same in both
modes.

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
it hex encoded, together with the Steam ID and name so a trusting project works
too:

```csharp
var ticket = await Steamworks.SteamUser.GetAuthTicketForWebApiAsync(AuthService.SteamTicketIdentity);
var hex = BitConverter.ToString(ticket.Data).Replace("-", "");
await PurrServices.instance.auth.LoginWithSteamAsync(
    hex, Steamworks.SteamClient.Name, Steamworks.SteamClient.SteamId.ToString());
ticket.Cancel();
```

Errors: `400` the ticket (verifying) or Steam ID (trusting) is missing, `401`
Steam rejected the ticket, `403` refused by your options (family sharing, bans),
`502` Steam rejected the project's Web API key, `503` Steam unreachable or Steam
sign-in not configured.
