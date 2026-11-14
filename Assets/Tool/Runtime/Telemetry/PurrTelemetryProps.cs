using System;
using System.Collections.Generic;

namespace PurrNet.Services.Telemetry
{
    public struct PurrTelemetryProps : IDisposable
    {
        static readonly Stack<Dictionary<string, object>> _pool = new Stack<Dictionary<string, object>>();

        Dictionary<string, object> _dict;
        bool _allocated;

        static Dictionary<string, object> RentDictionary()
        {
            lock (_pool)
            {
                if (_pool.Count > 0)
                    return _pool.Pop();
            }

            return new Dictionary<string, object>();
        }

        public static PurrTelemetryProps Rent()
        {
            return new PurrTelemetryProps
            {
                _dict = RentDictionary(),
                _allocated = true
            };
        }

        public PurrTelemetryProps With(string key, object value)
        {
            if (!_allocated)
            {
                _dict = RentDictionary();
                _allocated = true;
            }
            _dict[key] = value;
            return this;
        }

        public object this[string key]
        {
            get => _allocated ? _dict[key] : null;
            set
            {
                if (!_allocated)
                {
                    _dict = RentDictionary();
                    _allocated = true;
                }
                _dict[key] = value;
            }
        }

        public int Count => _allocated ? _dict.Count : 0;

        internal Dictionary<string, object> RawDictionary => _allocated ? _dict : null;

        public void Dispose()
        {
            if (!_allocated) return;
            _dict.Clear();
            lock (_pool)
                _pool.Push(_dict);
            _dict = null;
            _allocated = false;
        }
    }
}
