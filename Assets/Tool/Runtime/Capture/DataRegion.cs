#if UNITY_WEBGL && !UNITY_EDITOR
#define PURRNET_WEB_REGION
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using PurrNet.Services.Capture;
using UnityEngine;
#if PURRNET_WEB_REGION
using System.Runtime.InteropServices;
#endif

namespace PurrNet.Services
{
    /// <summary>
    /// Whether this device looks like it is in a region whose privacy laws keep data gathering off: the EEA (the EU,
    /// Iceland, Liechtenstein, Norway), the UK and Switzerland. It is worked out on the device, from its time zone, its
    /// region setting and the country sources added with <see cref="AddCountrySource"/> (PurrServices.Steam adds Steam's
    /// country): nothing is sent anywhere to find out. A device is restricted when any of them points at such a
    /// country, and when none of them says where it is.
    /// <para>
    /// A person can decide for their own machine with the <see cref="EnvironmentVariable"/> environment variable:
    /// <c>1</c> allows gathering there whatever the region, <c>0</c> never allows it. A game decides for its players
    /// with <see cref="overrideRestricted"/>, which comes first.
    /// </para>
    /// </summary>
    public static class DataRegion
    {
        const int RefreshMs = 30000;

        static readonly HashSet<string> RestrictedCountries = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "AT", "BE", "BG", "HR", "CY", "CZ", "DK", "EE", "FI", "FR", "DE", "GR", "HU", "IE", "IT", "LV", "LT", "LU",
            "MT", "NL", "PL", "PT", "RO", "SK", "SI", "ES", "SE",
            "AX", "GF", "GP", "MQ", "RE", "YT", "MF",
            "IS", "LI", "NO",
            "GB", "UK", "GG", "JE", "IM", "GI",
            "CH"
        };

        static readonly HashSet<string> RestrictedZones = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "GMT Standard Time", "Greenwich Standard Time", "W. Europe Standard Time", "Central Europe Standard Time",
            "Romance Standard Time", "Central European Standard Time", "E. Europe Standard Time", "FLE Standard Time",
            "GTB Standard Time", "Azores Standard Time",
            "Atlantic/Azores", "Atlantic/Madeira", "Atlantic/Canary", "Atlantic/Reykjavik", "Atlantic/Faroe",
            "Arctic/Longyearbyen", "Asia/Nicosia", "Asia/Famagusta", "Africa/Ceuta", "America/Guadeloupe",
            "America/Martinique", "America/Cayenne", "America/Marigot", "Indian/Reunion", "Indian/Mayotte",
            "GB", "GB-Eire", "Eire", "Iceland", "Portugal", "Poland", "CET", "EET", "WET", "MET"
        };

        static readonly HashSet<string> UnknownZones = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Local", "UTC", "Etc/UTC", "Etc/UCT", "Etc/GMT", "Etc/Universal", "Etc/Zulu", "GMT", "UCT", "Universal", "Zulu",
            "Coordinated Universal Time"
        };

        static readonly List<Func<string>> CountrySources = new List<Func<string>>();

        /// <summary>
        /// Nothing made in the editor or on a CI machine is ever sent, so where their players are does not keep
        /// them off. Tests turn it off to see what a build does.
        /// </summary>
        internal static bool ignoreInEditorAndCI = Application.isEditor || CaptureEnvironment.runningOnCI;

        /// <summary>Whether data about this machine's own player may be gathered.</summary>
        public static bool dataCollectionAllowed => ignoreInEditorAndCI || !isRestricted;

        /// <summary>The environment variable a person sets to decide for their own machine: 1 allows, 0 forbids.</summary>
        public const string EnvironmentVariable = "PURRSERVICES_DATA_COLLECTION";

        static readonly bool? EnvironmentRestricted = ParseEnvironment(Read(() => Environment.GetEnvironmentVariable(EnvironmentVariable)));

        /// <summary>What a value of <see cref="EnvironmentVariable"/> means for <see cref="isRestricted"/>; null when it says nothing.</summary>
        internal static bool? ParseEnvironment(string value)
        {
            switch (value?.Trim().ToLowerInvariant())
            {
                case "1":
                case "true":
                case "on":
                case "yes":
                    return false;
                case "0":
                case "false":
                case "off":
                case "no":
                    return true;
                default:
                    return null;
            }
        }

        static bool _checked;
        static bool _restricted;
        static int _checkedAt;

        /// <summary>
        /// Replaces what the device says: true or false decide <see cref="isRestricted"/>, null goes back to the
        /// device. For games that ask their players for consent, and for development machines.
        /// </summary>
        public static bool? overrideRestricted { get; set; }

        /// <summary>True when data about this device's player must not be gathered.</summary>
        public static bool isRestricted
        {
            get
            {
                if (overrideRestricted.HasValue)
                    return overrideRestricted.Value;
                if (EnvironmentRestricted.HasValue)
                    return EnvironmentRestricted.Value;

                int now = Environment.TickCount;
                if (!_checked || unchecked(now - _checkedAt) > RefreshMs)
                {
                    _restricted = Check();
                    _checked = true;
                    _checkedAt = now;
                }
                return _restricted;
            }
        }

        /// <summary>
        /// Adds a source of the device's country as an ISO 3166 two-letter code (a platform's account or IP
        /// country). It returns null or an empty string, or throws, when it does not know.
        /// </summary>
        public static void AddCountrySource(Func<string> source)
        {
            if (source == null || CountrySources.Contains(source))
                return;
            CountrySources.Add(source);
            _checked = false;
        }

        public static bool IsRestrictedCountry(string isoCode) =>
            !string.IsNullOrEmpty(isoCode) && RestrictedCountries.Contains(isoCode.Trim());

        /// <summary>Whether an IANA (Europe/Berlin) or Windows (W. Europe Standard Time) time zone is in a restricted region.</summary>
        public static bool IsRestrictedTimeZone(string timeZoneId)
        {
            if (string.IsNullOrEmpty(timeZoneId))
                return false;
            timeZoneId = timeZoneId.Trim();
            return timeZoneId.StartsWith("Europe/", StringComparison.OrdinalIgnoreCase) || RestrictedZones.Contains(timeZoneId);
        }

        static bool Check()
        {
            bool known = false;

            string zone = Read(LocalTimeZone);
            if (!string.IsNullOrEmpty(zone) && !UnknownZones.Contains(zone.Trim()))
            {
                if (IsRestrictedTimeZone(zone))
                    return true;
                known = true;
            }

            if (Country(Read(SystemCountry), ref known))
                return true;

            for (int i = 0; i < CountrySources.Count; i++)
            {
                if (Country(Read(CountrySources[i]), ref known))
                    return true;
            }

            return !known;
        }

        static bool Country(string code, ref bool known)
        {
            code = code?.Trim();
            if (code == null || code.Length != 2 || !char.IsLetter(code[0]) || !char.IsLetter(code[1]) ||
                string.Equals(code, "IV", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(code, "ZZ", StringComparison.OrdinalIgnoreCase))
                return false;
            if (IsRestrictedCountry(code))
                return true;
            known = true;
            return false;
        }

        static string Read(Func<string> source)
        {
            try
            {
                return source();
            }
            catch (Exception)
            {
                return null;
            }
        }

#if PURRNET_WEB_REGION
        [DllImport("__Internal")]
        static extern string PurrNetRegion_TimeZone();

        [DllImport("__Internal")]
        static extern string PurrNetRegion_Language();

        static string LocalTimeZone() => PurrNetRegion_TimeZone();

        static string SystemCountry()
        {
            string language = PurrNetRegion_Language();
            int dash = language?.LastIndexOfAny(new[] { '-', '_' }) ?? -1;
            return dash >= 0 ? language.Substring(dash + 1) : null;
        }
#else
        static string LocalTimeZone() => TimeZoneInfo.Local.Id;

        static string SystemCountry() => RegionInfo.CurrentRegion.TwoLetterISORegionName;
#endif
    }
}
