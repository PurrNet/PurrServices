// Compiled only when Steamworks.NET (com.rlabrecque.steamworks.net) is installed; see
// PurrServices.Steam.asmdef. Mirrors Steamworks.NET's own platform guard so switching the
// Editor to a console or mobile target does not break the build.
#if !(UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX)
#define DISABLESTEAMWORKS
#endif

#if !DISABLESTEAMWORKS
using System;
using System.Text;
using System.Threading.Tasks;
using Steamworks;

namespace PurrNet.Services
{
    /// <summary>
    /// Steam sign-in for PurrServices with Steamworks.NET. Steam must already be initialized
    /// (SteamAPI.Init) and its callbacks pumped (SteamAPI.RunCallbacks every frame), as
    /// SteamManager or PurrNet's Steam transport do.
    /// <code>
    /// var result = await PurrSteamAuth.LoginAsync();
    /// if (!result.success) Debug.LogError(result.error);
    /// </code>
    /// </summary>
    public static class PurrSteamAuth
    {
        /// <summary>
        /// Gets a Web API ticket for <see cref="AuthService.SteamTicketIdentity"/> and signs in
        /// with it. The server names the player from Steam; <paramref name="displayName"/> is
        /// only used if Steam will not say (defaults to the local persona name).
        /// </summary>
        public static async Task<AuthResult> LoginAsync(string displayName = null, float timeoutSeconds = 10f)
        {
            var ticket = await GetWebApiTicketAsync(timeoutSeconds);
            if (ticket.error != null)
                return new AuthResult { success = false, error = ticket.error };

            try
            {
                if (string.IsNullOrEmpty(displayName))
                    displayName = SteamFriends.GetPersonaName();

                return await PurrServices.instance.auth.LoginWithSteamAsync(ticket.hex, displayName);
            }
            finally
            {
                // The server has used it (or never will); a Web API ticket should not outlive that.
                SteamUser.CancelAuthTicket(ticket.handle);
            }
        }

        /// <summary>
        /// Requests a Steam Web API ticket for PurrServices and returns it hex encoded. The caller
        /// owns the handle and must pass it to <c>SteamUser.CancelAuthTicket</c> once the ticket
        /// has been used. Call from the main thread.
        /// </summary>
        public static async Task<(HAuthTicket handle, string hex, string error)> GetWebApiTicketAsync(float timeoutSeconds = 10f)
        {
            var tcs = new TaskCompletionSource<(EResult result, string hex)>(TaskCreationOptions.RunContinuationsAsynchronously);
            var handle = HAuthTicket.Invalid;

            Callback<GetTicketForWebApiResponse_t> callback = null;
            callback = Callback<GetTicketForWebApiResponse_t>.Create(response =>
            {
                // Registered per callback type: another ticket request in the game lands here too.
                if (response.m_hAuthTicket.m_HAuthTicket != handle.m_HAuthTicket)
                    return;

                if (response.m_eResult != EResult.k_EResultOK)
                {
                    tcs.TrySetResult((response.m_eResult, null));
                    return;
                }

                tcs.TrySetResult((response.m_eResult, ToHex(response.m_rgubTicket, response.m_cubTicket)));
            });

            try
            {
                try
                {
                    handle = SteamUser.GetAuthTicketForWebApi(AuthService.SteamTicketIdentity);
                }
                catch (InvalidOperationException)
                {
                    return (HAuthTicket.Invalid, null, "Steam is not initialized (call SteamAPI.Init first)");
                }

                if (handle.m_HAuthTicket == HAuthTicket.Invalid.m_HAuthTicket)
                    return (HAuthTicket.Invalid, null, "Steam refused to create an auth ticket");

                var finished = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds)));
                if (finished != tcs.Task)
                {
                    SteamUser.CancelAuthTicket(handle);
                    return (HAuthTicket.Invalid, null,
                        "Timed out waiting for the Steam ticket (is SteamAPI.RunCallbacks() called every frame?)");
                }

                var (result, hex) = tcs.Task.Result;
                if (hex == null)
                {
                    SteamUser.CancelAuthTicket(handle);
                    return (HAuthTicket.Invalid, null, $"Steam could not issue a ticket: {result}");
                }

                return (handle, hex, null);
            }
            finally
            {
                callback.Dispose();
            }
        }

        static string ToHex(byte[] bytes, int length)
        {
            var sb = new StringBuilder(length * 2);
            for (var i = 0; i < length; i++)
                sb.Append(bytes[i].ToString("x2"));
            return sb.ToString();
        }
    }
}
#endif
