#if PURRNET_RECORDING_HOST
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using PurrNet.Recording;
using PurrNet.Transports;
using Object = UnityEngine.Object;

[assembly: InternalsVisibleTo("PurrServices.Recording.Tests")]

// The other halves of the PurrNet types the recorder reads: PurrNet declares them partial and calls partial methods
// that only exist while this package is installed.
namespace PurrNet
{
    public partial class NetworkModule
    {
        /// <summary>Row type of this module's recorded state, or null when it is not recorded.</summary>
        internal virtual Type recordedType => null;

        /// <summary>Writes this module's state for <paramref name="tick"/> as one <see cref="recordedType"/> row.</summary>
        internal virtual void Record(Recording.RecordingSession session, Recording.StreamRows rows, ulong tick) { }

        /// <summary>Whether this module says when its recorded state changes; otherwise a recorder asks every tick.</summary>
        internal virtual bool recordedOnChange => false;

        /// <summary>Set by the recorder that records this module.</summary>
        internal Recording.RecordedLife recordedLife;

        /// <summary>Whether a value of this type only changes when it is set (plain data or a string).</summary>
        internal static bool RecordedPlain(Type type) =>
            type == typeof(string) || Unity.Collections.LowLevel.Unsafe.UnsafeUtility.IsUnmanaged(type);

        partial void OnRecordedChanged()
        {
            if (recordedLife != null)
                recordedLife.changed = true;
        }
    }

    public partial class SyncVar<T>
    {
        internal override Type recordedType => typeof(Recording.RecordedValue<T>);

        // A string or plain data only changes when the value is set; anything else can change from the inside.
        static readonly bool _recordedOnChange = typeof(T) == typeof(string) ||
                                                 Unity.Collections.LowLevel.Unsafe.UnsafeUtility.IsUnmanaged(typeof(T));

        internal override bool recordedOnChange => _recordedOnChange;

        internal override void Record(Recording.RecordingSession session, Recording.StreamRows rows, ulong tick)
        {
            var row = new Recording.RecordedValue<T> { value = _value };
            session.AppendRow(rows, tick, ref row);
        }
    }

    public partial class SyncList<T>
    {
        internal override Type recordedType => typeof(Recording.RecordedValue<List<T>>);

        internal override void Record(Recording.RecordingSession session, Recording.StreamRows rows, ulong tick)
        {
            var row = new Recording.RecordedValue<List<T>> { value = _list };
            session.AppendRow(rows, tick, ref row);
        }
    }

    public partial class SyncArray<T>
    {
        internal override Type recordedType => typeof(Recording.RecordedValue<T[]>);

        static readonly bool _recordedOnChange = RecordedPlain(typeof(T));

        internal override bool recordedOnChange => _recordedOnChange;

        internal override void Record(Recording.RecordingSession session, Recording.StreamRows rows, ulong tick)
        {
            var row = new Recording.RecordedValue<T[]> { value = _array };
            session.AppendRow(rows, tick, ref row);
        }
    }

    public partial class SyncDictionary<TKey, TValue>
    {
        internal override Type recordedType => typeof(Recording.RecordedValue<Dictionary<TKey, TValue>>);

        static readonly bool _recordedOnChange = RecordedPlain(typeof(TKey)) && RecordedPlain(typeof(TValue));

        internal override bool recordedOnChange => _recordedOnChange;

        internal override void Record(Recording.RecordingSession session, Recording.StreamRows rows, ulong tick)
        {
            var row = new Recording.RecordedValue<Dictionary<TKey, TValue>> { value = _dict };
            session.AppendRow(rows, tick, ref row);
        }
    }

    public partial class SyncHashSet<T>
    {
        internal override Type recordedType => typeof(Recording.RecordedValue<HashSet<T>>);

        static readonly bool _recordedOnChange = RecordedPlain(typeof(T));

        internal override bool recordedOnChange => _recordedOnChange;

        internal override void Record(Recording.RecordingSession session, Recording.StreamRows rows, ulong tick)
        {
            var row = new Recording.RecordedValue<HashSet<T>> { value = _set };
            session.AppendRow(rows, tick, ref row);
        }
    }

    public partial class SyncInput<T>
    {
        internal override Type recordedType => typeof(Recording.RecordedValue<T>);

        internal override void Record(Recording.RecordingSession session, Recording.StreamRows rows, ulong tick)
        {
            var row = new Recording.RecordedValue<T> { value = _value };
            session.AppendRow(rows, tick, ref row);
        }
    }

    public partial class SyncQueue<T>
    {
        internal override Type recordedType => typeof(Recording.RecordedValue<Queue<T>>);

        static readonly bool _recordedOnChange = RecordedPlain(typeof(T));

        internal override bool recordedOnChange => _recordedOnChange;

        internal override void Record(Recording.RecordingSession session, Recording.StreamRows rows, ulong tick)
        {
            var row = new Recording.RecordedValue<Queue<T>> { value = _queue };
            session.AppendRow(rows, tick, ref row);
        }
    }

    public partial class SyncTimer
    {
        internal override Type recordedType => typeof(Recording.RecordedTimer);

        internal override void Record(Recording.RecordingSession session, Recording.StreamRows rows, ulong tick)
        {
            var row = new Recording.RecordedTimer { state = _state, remaining = _remaining };
            session.AppendRow(rows, tick, ref row);
        }
    }

    public static partial class UnityProxy
    {
        static partial void OnCreated(Object instance)
        {
            RecordedEffects.Created(instance);
        }

        static partial void OnDestroying(Object instance)
        {
            RecordedEffects.Destroyed(instance);
        }
    }

    internal readonly partial struct PlayerBroadcastCallback<T>
    {
        partial void RecordReceived(PlayerID playerId, in T data)
        {
            if (RecordedCalls.receiving != null)
                RecordedCalls.BroadcastReceived(playerId, data);
        }
    }
}

namespace PurrNet.Modules
{
    public partial class BroadcastModule
    {
        partial void RecordSent<T>(Connection conn, in T data, bool toAll)
        {
            if (RecordedCalls.enabled)
                RecordedCalls.BroadcastSent(_networkManager, conn, data, toAll);
        }

        partial void RecordSent<T>(IReadOnlyList<Connection> conn, in T data)
        {
            if (!RecordedCalls.enabled)
                return;
            for (var i = 0; i < conn.Count; i++)
                RecordedCalls.BroadcastSent(_networkManager, conn[i], data, false);
        }

        partial void RecordReceiving(bool begin)
        {
            if (!begin)
                RecordedCalls.receiving = null;
            else if (_asServer && RecordedCalls.enabled)
                RecordedCalls.receiving = _networkManager;
        }
    }

    internal readonly partial struct BroadcastCallback<T>
    {
        partial void RecordReceived(Connection conn, in T data)
        {
            if (RecordedCalls.receiving != null)
                RecordedCalls.BroadcastReceived(conn, data);
        }
    }
}
#endif
