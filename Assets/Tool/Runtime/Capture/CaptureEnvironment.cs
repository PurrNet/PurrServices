using System;

namespace PurrNet.Services.Capture
{
    /// <summary>What kind of machine this process runs on, as far as gathering data is concerned.</summary>
    public static class CaptureEnvironment
    {
        /// <summary>Whether this process runs on a CI machine (the CI environment variable and the like).</summary>
        public static bool runningOnCI { get; } = DetectCI();

        static bool DetectCI()
        {
            try
            {
                string ci = Environment.GetEnvironmentVariable("CI");
                if (!string.IsNullOrEmpty(ci) && ci != "0" && !ci.Equals("false", StringComparison.OrdinalIgnoreCase))
                    return true;
                foreach (var name in new[] { "GITHUB_ACTIONS", "GITLAB_CI", "TF_BUILD", "JENKINS_URL", "TEAMCITY_VERSION", "BUILDKITE" })
                {
                    if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
                        return true;
                }
            }
            catch (Exception)
            {
            }
            return false;
        }
    }
}
