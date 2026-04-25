using UnityEngine;

namespace PurrNet.Services.Telemetry
{
    [DefaultExecutionOrder(-1000)]
    internal sealed class PurrTelemetryRunner : MonoBehaviour
    {
        void Update()
        {
            PurrTelemetrySender.TickFromRunner(Time.unscaledDeltaTime);
        }

        void OnApplicationPause(bool paused)
        {
            if (paused)
                _ = PurrTelemetrySender.FlushAsync();
        }

        void OnApplicationQuit()
        {
            _ = PurrTelemetrySender.FlushAsync();
            PurrTelemetrySender.PersistPending();
        }
    }
}
