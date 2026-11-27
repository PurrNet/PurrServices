using System;
using System.Collections.Generic;
using UnityEngine;

namespace PurrNet.Services
{
    /// <summary>
    /// Names that could tell who this machine's player is: the operating system's user name, the device's name, and
    /// the ones from the sources added with <see cref="AddSource"/> (a platform name, the game's own player name).
    /// Data gathered about a game on this machine (recordings) has them replaced wherever they show up in its text.
    /// They are only ever used on this machine.
    /// </summary>
    public static class PrivateNames
    {
        const int ShortestName = 3;

        static readonly HashSet<string> Generic = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "user", "admin", "administrator", "root", "player", "guest", "unknown", "<unknown>", "localhost", "web"
        };

        static readonly List<Func<string>> Sources = new List<Func<string>>();
        static string[] _local;

        /// <summary>Changes when a source is added.</summary>
        public static int version { get; private set; }

        /// <summary>
        /// Adds a source of a name for this player, such as the platform's or the game's own. It returns null or throws
        /// when it has none.
        /// </summary>
        public static void AddSource(Func<string> source)
        {
            if (source == null || Sources.Contains(source))
                return;
            Sources.Add(source);
            version++;
        }

        /// <summary>Every name this machine knows for its player.</summary>
        public static void GetAll(List<string> into)
        {
            for (int i = 0; i < Sources.Count; i++)
                Add(into, Read(Sources[i]));
            if (_local == null)
            {
                var local = new List<string>();
                Add(local, Read(() => Environment.UserName));
                Add(local, Read(() => Environment.MachineName));
                Add(local, Read(() => SystemInfo.deviceName));
                _local = local.ToArray();
            }
            foreach (var name in _local)
                Add(into, name);
        }

        /// <summary>Whether a name is worth looking for: long enough, and not a word every machine has.</summary>
        public static bool Usable(string name) =>
            !string.IsNullOrWhiteSpace(name) && name.Trim().Length >= ShortestName && !Generic.Contains(name.Trim());

        static void Add(List<string> into, string name)
        {
            if (!Usable(name))
                return;
            name = name.Trim();
            for (int i = 0; i < into.Count; i++)
                if (string.Equals(into[i], name, StringComparison.OrdinalIgnoreCase))
                    return;
            into.Add(name);
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
    }
}
