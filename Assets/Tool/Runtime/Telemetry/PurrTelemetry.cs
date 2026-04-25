using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace PurrNet.Services.Telemetry
{
    public static class PurrTelemetry
    {
        static string _sourceOverride;

        public static bool isReady
        {
            get
            {
                var cfg = PurrTelemetryConfig.Load();
                return cfg != null && cfg.isReady;
            }
        }

        public static void SetSource(string source)
        {
            _sourceOverride = source;
        }

        internal static string CurrentSource =>
            _sourceOverride ?? (Application.isEditor ? "editor" : "runtime");

        public static void Track(string eventName)
        {
            if (string.IsNullOrEmpty(eventName)) return;
            PurrTelemetrySender.Enqueue(eventName, null);
        }

        public static void Track(string eventName, IReadOnlyDictionary<string, object> properties)
        {
            if (string.IsNullOrEmpty(eventName)) return;
            PurrTelemetrySender.Enqueue(eventName, properties);
        }

        public static void Track(string eventName, PurrTelemetryProps properties)
        {
            try
            {
                if (string.IsNullOrEmpty(eventName)) return;
                PurrTelemetrySender.Enqueue(eventName, properties.RawDictionary);
            }
            finally
            {
                properties.Dispose();
            }
        }

        public static Task FlushAsync() => PurrTelemetrySender.FlushAsync();

        public static void Flush() => _ = PurrTelemetrySender.FlushAsync();
    }
}
