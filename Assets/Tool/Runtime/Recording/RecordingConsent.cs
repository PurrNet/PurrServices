using PurrNet.Modules;
using PurrNet.Services;
using UnityEngine;

namespace PurrNet.Recording
{
    /// <summary>
    /// What a client tells the server about its own player when it connects: whether data about that player may be
    /// gathered (<see cref="DataRegion"/>). The server leaves such a player out of its recording. It is one short
    /// value in the connection's sign-in, and it is only sent while the recorder is part of the build.
    /// </summary>
    public static class RecordingConsent
    {
        const string ConnectionKey = "purrservices.dataCollection";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Register() =>
            AuthModule.SetConnectionData(ConnectionKey, () => DataRegion.dataCollectionAllowed ? "1" : "0");

        /// <summary>Server only. Whether the client of <paramref name="player"/> said data about its player may be gathered.</summary>
        public static bool IsAllowedFor(NetworkManager manager, PlayerID player) =>
            manager && manager.TryGetModule<AuthModule>(true, out var auth) &&
            manager.TryGetModule<PlayersManager>(true, out var players) &&
            players.TryGetConnection(player, out var conn) &&
            auth.TryGetConnectionData(conn, ConnectionKey, out var value) && value == "1";
    }
}
