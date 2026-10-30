# Matchmaking integration

PurrServices 1.3.0 supplies the Unity client for the website's competitive
matchmaking API. Enable the project's Matchmaking service and create queues in
its dashboard first. Each queue selects a team size, ranked/casual policy,
regions, skill limits, timeouts, season and either dedicated or player hosting.
The service forms two teams and preserves whole parties.

## Player client

`PurrServices.instance.matchmaking` uses the linked project's public key,
current player session, scope and game compatibility, just like lobbies. Ranked
queues require Steam or username/password authentication. Device identity is
supported only by casual queues.

Capture a client at the start of each search so an account or scope change
cannot redirect its later cleanup request to another identity:

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using PurrNet.Services;

var services = PurrServices.instance;
var matchmaking = services.matchmaking.CaptureSession();
var request = new MatchmakingTicketRequest
{
    queueId = "ranked-doubles",
    requestId = Guid.NewGuid().ToString("N"),
    partySize = 1,
    latencies = new Dictionary<string, float> { ["eu"] = measuredEuMilliseconds }
};
var created = await matchmaking.CreateTicketAsync(request);
if (!created.success)
    throw new InvalidOperationException(created.error);
var ticketId = created.data.id;
```

Measure latency against your game's region probes; the SDK does not substitute
made-up measurements. If a create response is lost, retry the **same** request
and `requestId`. A different search must use a new ID. Server-owned ratings are
never supplied in a ticket request.

`IMatchmakingClient` is available for provider integration and deterministic
tests. Its methods return `Task<HttpResult<T>>`:

| Method | Purpose |
| --- | --- |
| `ListQueuesAsync` | Discover enabled queues and their policies. |
| `CreateTicketAsync` | Create a solo or assembling party ticket. |
| `JoinTicketAsync(ticketId, inviteCode, latencies)` | Join a party using this player's own session. |
| `GetTicketAsync(ticketId)` | Poll state and heartbeat this player. |
| `CancelTicketAsync(ticketId)` | Cancel the whole party or decline its pre-game match. |
| `GetMatchAsync(matchId)` | Poll match state and private assignment; heartbeat this player. |
| `AcceptMatchAsync(matchId, accept)` | Explicitly accept or decline a ready check. |
| `PublishConnectionAsync(matchId, connection)` | Elected host acknowledges a listening game. |
| `GetRatingAsync(queueId)` | Read this player's queue/season rating. |
| `VerifyAdmissionAsync(matchId, playerId, connectionToken)` | Elected player host validates an incoming game connection. |
| `SubmitResultAsync(matchId, resultId, winningTeam)` | Casual player host reports a result. Ranked results require a trusted server. |

Every method accepts an optional `CancellationToken`. Calls must begin on
Unity's main thread. Transport cancellation throws `OperationCanceledException`;
HTTP and response errors return `success = false`, `statusCode`, and `error`.
`retryAfterSeconds` carries server backoff. Requests have a finite 15-second
timeout; a directly constructed `ServiceHttp` can set `timeoutSeconds` (1–300).
Existing lobby HTTP calls use the same finite timeout.

The SDK does not start hidden polling loops. Your controller should poll every
3–5 seconds with jitter, honor 429/Retry-After and back off on 503/network errors.
Cancel loops on logout, account change, teardown and terminal states. A create
may have committed even if local waiting is cancelled: retain its request ID,
resolve its ticket with the original captured client, and best-effort cancel it.
PurrLobby's service provider handles this lifecycle for its matchmaking flow.

## Party and ready-check flow

For a party, create with `partySize > 1`. While `assembling`, only the leader's
ticket response contains `inviteCode`. Share that code and `id` through your
party/lobby UI; each member calls `JoinTicketAsync` using their own account.
Never collect or share other players' session tokens. Every member must poll.

A full ticket is `queued`; a selected ticket becomes `matched` and supplies a
`matchId`. Poll the match and present a ready check while it is `ready`. Every
player must explicitly accept. On a failed ready check, poll the ticket again:
fully accepted parties may be requeued with their original age. Other parties
leave the queue, and decliners/nonresponders receive a short cooldown.

Match states are `ready`, `allocating`, `active`, `completed`, `cancelled`, and
`expired`. Ticket terminal states are `completed`, `cancelled`, and `expired`.
Do not interpret cancellation of local polling as server cancellation.

## Hosting and game admission

For dedicated hosting, players wait for `active` and a non-null `connection`.
They always join as clients. A trusted backend performs allocation below.

For player hosting, the elected host sees `isHost = true` and a provisional
connection during `allocating`. Its `serverAddress` defaults to a unique relay
room. Load the game and start listening, then call `PublishConnectionAsync`.
Other players receive the connection only after that acknowledgment. The SDK
publishes only `serverAddress` and `serverPort`; it never echoes private
admission tokens or trusts a caller-supplied host ID.

Each player's `connection.connectionToken` is a bearer credential specific to
that player and match. Send it in the game's authentication handshake along
with the player's ID. The server/host must call its admission verification
method and reject the connection unless `success && data.valid`. Bind the
authenticated connection to `data.playerId` and `data.team`. Never log or send
this token through lobby metadata or chat. It supports reconnects until the
match ends; it does not independently configure or secure a transport.

PurrLobby's matchmaking authenticator and allocators perform this handshake.
For another networking front end, supply an equivalent authenticator before
starting gameplay.

## Trusted dedicated-server client

`MatchmakingServerClient` is separate from the public player client. Construct
it only in a trusted runtime, using a secret supplied by your server environment:

```csharp
var server = new MatchmakingServerClient(
    serviceUrl,
    Environment.GetEnvironmentVariable("PURR_MATCHMAKING_SECRET"),
    scope: "production",
    compatibility: gameBuild);

var claim = await server.ClaimAsync("allocator-1", queueId: "ranked-doubles");
if (claim.success && claim.data.match != null)
{
    // Start/provision the game's server outside this service.
    // Renew while starting; publish only once its listener is ready.
    var renewed = await server.RenewClaimAsync(
        claim.data.match.id, "allocator-1", claim.data.claimToken);
}
```

Do not put the secret in `ApplicationConstants`, a ScriptableObject, an
Inspector field, or a player build. The ordinary constructor rejects public
and user keys. The backend independently enforces secret-key authorization.
This client uses Unity HTTP APIs; call it on the Unity main thread. An external
non-Unity allocator can instead call the documented REST endpoints directly.

Server methods:

- `ClaimAsync(workerId, queueId?, region?)`: claim an accepted dedicated match;
  `data.match == null` means idle.
- `RenewClaimAsync(matchId, workerId, claimToken)`: renew before `claimUntil`;
  claims last at most 20 seconds and cannot extend the allocation deadline.
- `PublishConnectionAsync(matchId, claimToken, connection)`: acknowledge the
  ready server. A superseded claim cannot publish.
- `VerifyAdmissionAsync(matchId, playerId, connectionToken)`: validate each
  incoming player before accepting them into gameplay.
- `SubmitResultAsync(matchId, resultId, winningTeam)`: atomically finish a game
  and apply server-owned ratings. Team indices are 0/1; `null` is a draw.
- `CancelMatchAsync(matchId, reason)`: release an invalid/failed game without
  rating changes. Reasons are `AllocationFailed`, `ServerUnavailable`, or
  `InvalidMatch`.

Reuse the same `resultId` and outcome for retries. Conflicting outcomes return
409. Clean up any provisioned server whose claim is lost. Peer hosts never
receive these privileges: ranked peer-hosted games need your trusted result
verification backend, which decides disconnects/forfeits and validates evidence.

## Validation

The package's Editor test assembly is `PurrServices.Matchmaking.Tests`. It
checks request/response contracts, party enrollment payloads, private token
handling, cancellation propagation, captured login identity, claim fencing
payloads, draw serialization and separate player/server routes. Network play
tests should exercise separate host/client processes, failed ready checks,
host startup timeout, server claim renewal, reconnects and logout mid-search.
