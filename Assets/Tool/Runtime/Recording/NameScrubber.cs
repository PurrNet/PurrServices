using System;
using System.Collections.Generic;
using System.Text;
using PurrNet.Services;

namespace PurrNet.Recording
{
    /// <summary>
    /// Replaces the names this machine's player is known by (<see cref="PrivateNames"/>) with <c>&lt;player N&gt;</c>
    /// in the text a recording holds (<see cref="RecordingSession.textFilter"/>). A name is replaced where it stands as
    /// a word of its own, whatever its case.
    /// </summary>
    sealed class NameScrubber
    {
        const int MaxCached = 4096;

        readonly List<(string name, string replacement)> _names = new List<(string, string)>();
        readonly Dictionary<string, string> _cache = new Dictionary<string, string>(StringComparer.Ordinal);
        readonly List<string> _scratch = new List<string>();

        bool _built;
        int _localVersion;
        PlayerID? _local;

        /// <summary>Looks again at the names to replace. True when they changed.</summary>
        public bool Refresh(PlayerID? localPlayer)
        {
            if (_built && _localVersion == PrivateNames.version && Nullable.Equals(localPlayer, _local))
                return false;

            _built = true;
            _localVersion = PrivateNames.version;
            _local = localPlayer;
            Rebuild();
            return true;
        }

        void Rebuild()
        {
            _names.Clear();
            _cache.Clear();

            _scratch.Clear();
            PrivateNames.GetAll(_scratch);
            string local = Replacement(_local);
            foreach (var name in _scratch)
                Add(name, local);

            _names.Sort((a, b) => b.name.Length.CompareTo(a.name.Length));
        }

        void Add(string name, string replacement)
        {
            for (int i = 0; i < _names.Count; i++)
                if (string.Equals(_names[i].name, name, StringComparison.OrdinalIgnoreCase))
                    return;
            _names.Add((name, replacement));
        }

        static string Replacement(PlayerID? player) => player.HasValue ? $"<player {player.Value.id.value}>" : "<player>";

        public string Scrub(string text)
        {
            if (_names.Count == 0 || string.IsNullOrEmpty(text))
                return text;
            if (_cache.TryGetValue(text, out var done))
                return done;

            string result = text;
            for (int i = 0; i < _names.Count; i++)
                result = Replace(result, _names[i].name, _names[i].replacement);

            if (_cache.Count >= MaxCached)
                _cache.Clear();
            _cache[text] = result;
            return result;
        }

        static string Replace(string text, string name, string replacement)
        {
            int at = text.IndexOf(name, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
                return text;

            StringBuilder result = null;
            int from = 0;
            while (at >= 0)
            {
                int end = at + name.Length;
                bool word = (at == 0 || !char.IsLetterOrDigit(text[at - 1])) && (end == text.Length || !char.IsLetterOrDigit(text[end]));
                if (word)
                {
                    result ??= new StringBuilder(text.Length);
                    result.Append(text, from, at - from).Append(replacement);
                    from = end;
                }
                int next = word ? end : at + 1;
                at = next < text.Length ? text.IndexOf(name, next, StringComparison.OrdinalIgnoreCase) : -1;
            }

            if (result == null)
                return text;
            result.Append(text, from, text.Length - from);
            return result.ToString();
        }
    }
}
