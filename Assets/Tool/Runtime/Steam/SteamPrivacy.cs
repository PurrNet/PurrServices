#if !(UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX)
#define DISABLESTEAMWORKS
#endif

#if !DISABLESTEAMWORKS && PURRNET_RELAY_SIGN_IN
using UnityEngine;

namespace PurrNet.Services
{
    /// <summary>
    /// Adds the country Steam places this machine's IP in to <see cref="DataRegion"/> and, while the recorder is part
    /// of the build, the player's Steam name to its private names.
    /// </summary>
    static class SteamPrivacy
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Register()
        {
            DataRegion.AddCountrySource(Steamworks.SteamUtils.GetIPCountry);
#if PURRNET_RECORDING_ENABLED
            PrivateNames.AddSource(Steamworks.SteamFriends.GetPersonaName);
#endif
        }
    }
}
#endif
