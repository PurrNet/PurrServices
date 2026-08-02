namespace PurrNet.Services.Telemetry
{
    public static class PurrTelemetrySettings
    {
        public const int FlushBatchThreshold = 25;
        public const int FlushIntervalSeconds = 30;
        public const int MaxBatchSize = 50;
        public const int MaxRetries = 4;

        public static string publicKey => PurrServicesSettings.apiKey;

        public static string projectId => PurrServicesSettings.projectId;

        public static string projectName => PurrServicesSettings.projectName;

        public static string baseUrl => PurrServicesSettings.serverUrl;

        public static bool isLinked =>
            !string.IsNullOrEmpty(publicKey) && !string.IsNullOrEmpty(projectId);
    }
}
