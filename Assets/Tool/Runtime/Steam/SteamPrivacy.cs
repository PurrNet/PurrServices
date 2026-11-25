#if !(UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX)
#define DISABLESTEAMWORKS
#endif

#if !DISABLESTEAMWORKS && !PURRNET_RECORDING_LEGACY
using UnityEngine;

namespace PurrNet.Services
{
    /// <summary>Adds the country Steam places this machine's IP in to <see cref="DataRegion"/>, and the player's Steam name to <see cref="PrivateNames"/>.</summary>
    static class SteamPrivacy
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Register()
        {
            DataRegion.AddCountrySource(Steamworks.SteamUtils.GetIPCountry);
            PrivateNames.AddSource(Steamworks.SteamFriends.GetPersonaName);
        }
    }
}
#endif
