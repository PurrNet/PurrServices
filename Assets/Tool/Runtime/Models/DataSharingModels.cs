using System;
using Newtonsoft.Json;

namespace PurrNet.Services
{
    /// <summary>
    /// Whether the project shares anonymous data with PurrNet, as its owner chose on the project's billing tab on
    /// purrnet.dev. See <see cref="DataSharingService"/>.
    /// </summary>
    [Serializable]
    public struct DataSharingStatus
    {
        /// <summary>False once the project's owner opted out of sharing anonymous data with PurrNet.</summary>
        [JsonProperty("dataSharing")]
        public bool dataSharing;

        /// <summary>
        /// Whether recordings are wanted from this project: it shares data and its recordings service is on.
        /// False means record nothing, on the server, the host and every client alike.
        /// </summary>
        [JsonProperty("record")]
        public bool record;
    }
}
