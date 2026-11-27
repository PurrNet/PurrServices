# Relay capture

When a game is linked to a PurrNet project and plays through PurrTransport, PurrServices can keep what is needed to
look at a match again later: the traffic the relay already carries, a small file from each player's device, and a
description of the build. This page says what is gathered, when, and how to turn it off.

It needs PurrNet 1.26.0-beta.48 or newer. With an older PurrNet none of this is compiled in.

## When it runs

All of these must hold, otherwise nothing is gathered:

- The build is a player build (never the editor, never a CI machine) of a project linked through
  Tools > PurrNet > PurrServices.
- The game is connected through PurrTransport with that project's key.
- The project shares data. The owner's data-sharing choice on purrnet.dev decides; the game asks once when a
  PurrTransport session starts and does what it was last told when it cannot ask.
- For the player's own file: the device is not in a region where data gathering is off (`DataRegion`), and the game
  did not turn capture off.

## What is gathered

**On the relay.** A copy of the packets the relay forwards for the room, with who connected when. Nothing is added
to the game's traffic. Each connection's sign-in also says which build it is and carries a random name for that run
of the game, so the pieces can be matched afterwards.

**From each player's device.** One file per match, written to the temporary folder
(`Application.temporaryCachePath/PurrServices/Capture`), sent after the match and then deleted:

- the pose and lens of `Camera.main`, once per network tick, when it changed;
- raw input (pointer, scroll, mouse buttons, held keys, gamepad) and Input System action values, when they changed.
  No keys are recorded while a text field has focus;
- once a second, how the device's tick and clock line up with the server's.

It comes to roughly 1 MB per hour of play. A file that cannot be sent waits at most seven days, and at most 32 MB
wait in all. Nothing is sent during a match.

**From a release build, in the editor.** After a build that is not a development build, the editor shows
"Syncing with PurrServices" among its background tasks and sends:

- the build's id, Unity and PurrNet versions, platform and scripting defines;
- the script assemblies that use PurrNet (they say what every network message looks like);
- the network prefab lists, and for each network prefab and each scene in the build list: its network components
  and its colliders, with collision meshes and terrain heights.

Nothing about how the game looks is sent: no renderers, drawn meshes, materials, textures or audio. Inspector values
are sent for PurrNet's own components only, not for the game's scripts.

The build itself is not slowed down. Prefabs are described afterwards, one at a time and only while Unity is not
the window being worked in; uploads are paced; a prefab or mesh that was sent before is not sent again. Without a
network nothing waits: the data stays in `Library/PurrServices/Sync` for the next build. A build made from the
command line describes prefabs for at most twenty seconds and gives up after three minutes in all.

## Turning it off

- **For a project:** turn data sharing off on purrnet.dev. Games stop gathering at their next session, and what
  waits on players' devices is deleted unsent.
- **In a game, for a player:** `RelayCapture.enabled = false` (for example from a settings screen) stops the
  player's own file and deletes what waits. `DataRegion.overrideRestricted` decides whether the device counts as
  one where gathering is off: `true` never gathers, `false` allows it (for games that ask for consent), `null`
  goes back to what the device says.
- **On one machine:** the environment variable `PURRSERVICES_DATA_COLLECTION` set to `0` never gathers there, and
  `1` allows it whatever the region. `overrideRestricted` comes first when a game sets it.
- **Mobile data:** `RelayCapture.uploadOverMobileData = false` makes files wait for another network.
