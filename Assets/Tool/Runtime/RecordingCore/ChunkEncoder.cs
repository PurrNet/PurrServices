#if PURRNET_RECORDING_HOST
using System;
using System.Collections.Generic;
using Unity.Collections.LowLevel.Unsafe;
using Mathf = UnityEngine.Mathf;

namespace PurrNet.Recording
{
    public sealed class RecordedLife
    {
        public ulong tick;
        public bool changed;
    }

    public sealed class StreamRows
    {
        public int id;
        public RowLayout layout;
        public bool forwardFill;
        public RecordedLife life;
        internal ulong aliveTick;
        internal int lastOffset;
        public StreamRows baseStream;
        public int[] baseColumns;
        internal int slot = -1;
        internal int rowCount;
        internal RecordingBuffer[] lastBytes = Array.Empty<RecordingBuffer>();
        internal object[] lastObject = Array.Empty<object>();
        internal int[] lastVersion = Array.Empty<int>();
        internal object box;
        internal bool retired;

        public bool hasRows => rowCount > 0;

        internal ulong lastTick => life != null && life.tick > aliveTick ? life.tick : aliveTick;

        public void Retire()
        {
            aliveTick = lastTick;
            life = null;
            retired = true;
        }
    }

    public struct ChunkStream
    {
        public int id;
        public RowLayout layout;
        public bool forwardFill;
        public int[] baseColumns;
        public int baseSlot;
        public ulong lastTick;
    }

    public sealed class ChunkLog
    {
        public readonly RecordingBuffer bytes;
        public readonly List<ChunkStream> streams = new List<ChunkStream>();
        public ulong firstTick;
        public ulong tickCount;

        /// <summary>Which file of the session the chunk belongs to: the encoder forgets what it held between files.</summary>
        public int file;
        public const int TickFollows = int.MinValue;
        internal ulong lastTick;
        internal bool hasTick;

        public ChunkLog() => bytes = new RecordingBuffer(1 << 16);

        public ChunkLog(ByteShelf shelf) => bytes = new RecordingBuffer(shelf, 1 << 16);

        public void Clear()
        {
            bytes.Clear();
            streams.Clear();
            hasTick = false;
        }
    }

    public sealed class ChunkEncoder
    {
        static readonly uint[] CrcTables = BuildCrcTables();

        static uint[] BuildCrcTables()
        {
            var tables = new uint[8 * 256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                tables[i] = c;
            }
            for (int t = 1; t < 8; t++)
                for (int i = 0; i < 256; i++)
                {
                    uint previous = tables[(t - 1) * 256 + i];
                    tables[t * 256 + i] = (previous >> 8) ^ tables[previous & 0xFF];
                }
            return tables;
        }

        readonly ByteShelf _shelf = ByteShelf.shared;
        readonly RecordingBuffer _buffer, _work, _coded;

        public ChunkEncoder()
        {
            _buffer = new RecordingBuffer(_shelf, 4096);
            _work = new RecordingBuffer(_shelf, 1024);
            _coded = new RecordingBuffer(_shelf, 1024);
        }
        readonly List<int> _keep = new List<int>();

        /// <summary>
        /// The last row (as recorded, before grid and XOR) and managed values of every per-tick stream, and through
        /// which tick of which chunk it holds. A stream whose rows in a chunk all repeat it is not written again: the
        /// chunk ends with a list of such streams (HOLDS), and readers keep showing the row they have.
        /// </summary>
        sealed class Held
        {
            public byte[] row;
            public byte[][] managed;
            public ulong end;
            public int chunk;
        }

        readonly Dictionary<int, Held> _held = new Dictionary<int, Held>();
        readonly List<long> _holds = new List<long>();
        readonly List<int> _stale = new List<int>();
        bool[] _isBase = Array.Empty<bool>();
        int[] _previousOfKey = Array.Empty<int>();
        readonly Dictionary<int, int> _lastOfKey = new Dictionary<int, int>();
        int _file = -1;
        int _chunkIndex;
        int _blocksAt;
        int _written;
        readonly Dictionary<RowLayout, bool[]> _numericMasks = new Dictionary<RowLayout, bool[]>();
        byte[] _plane = Array.Empty<byte>();

        int[] _slotFirst = Array.Empty<int>();
        int[] _slotCount = Array.Empty<int>();
        int[] _slotFill = Array.Empty<int>();
        int[] _slotManagedFirst = Array.Empty<int>();
        byte[] _rows = Array.Empty<byte>();
        int[] _rowOffset = Array.Empty<int>();
        ulong[] _ticks = Array.Empty<ulong>();
        int[] _valueStart = Array.Empty<int>();
        int[] _valueLength = Array.Empty<int>();
        int[] _entryOf = Array.Empty<int>();

        static void Grow<T>(ref T[] array, int size)
        {
            if (array.Length < size)
                array = new T[Math.Max(Mathf.NextPowerOfTwo(size), array.Length * 2)];
        }

        public void Reserve(int logCapacity)
        {
            Grow(ref _rows, logCapacity);
            Grow(ref _rowOffset, logCapacity / 16);
            Grow(ref _ticks, logCapacity / 16);
            _buffer.Reserve(logCapacity);
        }

        public RecordingBuffer Encode(ChunkLog log)
        {
            Begin(log);
            Step(long.MaxValue);
            return _buffer;
        }

        enum Phase { Count, Layout, Scatter, Header, Streams, Done }

        ChunkLog _log;
        Phase _phase = Phase.Done;
        int _at, _slot;
        ulong _tick;

        public RecordingBuffer result => _buffer;

        public void Begin(ChunkLog log)
        {
            if (log.file != _file)
            {
                _file = log.file;
                _held.Clear();
                _chunkIndex = 0;
            }
            _holds.Clear();
            _written = 0;
            _log = log;
            _phase = Phase.Count;
            _at = 0;
            _tick = 0;
            _slot = 0;
            int slots = log.streams.Count;
            Grow(ref _slotFirst, slots);
            Grow(ref _slotCount, slots);
            Grow(ref _slotFill, slots);
            Grow(ref _slotManagedFirst, slots);
            Array.Clear(_slotCount, 0, slots);
            Grow(ref _isBase, slots);
            Array.Clear(_isBase, 0, slots);
            for (int i = 0; i < slots; i++)
                if (log.streams[i].baseSlot >= 0)
                    _isBase[log.streams[i].baseSlot] = true;
        }

        public bool Step(long deadline)
        {
            var log = _log;
            var streams = log.streams;
            byte[] data = log.bytes.data;
            int end = log.bytes.length;
            int work = 0;
            while (true)
            {
                switch (_phase)
                {
                    case Phase.Count:
                        while (_at < end)
                        {
                            if ((++work & 255) == 0 && System.Diagnostics.Stopwatch.GetTimestamp() > deadline)
                                return false;
                            int header = ReadInt(data, _at);
                            int slot = header & ~ChunkLog.TickFollows;
                            _at += header < 0 ? 12 : 4;
                            _slotCount[slot]++;
                            _at = SkipEntry(data, _at, streams[slot].layout);
                        }
                        _phase = Phase.Layout;
                        break;

                    case Phase.Layout:
                        Layout(streams);
                        _at = 0;
                        _tick = 0;
                        _phase = Phase.Scatter;
                        break;

                    case Phase.Scatter:
                        while (_at < end)
                        {
                            if ((++work & 255) == 0 && System.Diagnostics.Stopwatch.GetTimestamp() > deadline)
                                return false;
                            _at = ScatterEntry(data, _at, streams);
                        }
                        _phase = Phase.Header;
                        break;

                    case Phase.Header:
                        _buffer.Clear();
                        _buffer.WriteVarUInt(log.firstTick);
                        _buffer.WriteVarUInt(log.tickCount);
                        // The count is known once the streams that only hold are left out: room for it now, filled in
                        // at the end as a varint padded to five bytes (readers take it like any other).
                        _blocksAt = _buffer.length;
                        for (int i = 0; i < 5; i++)
                            _buffer.WriteByte(0);
                        _slot = 0;
                        _phase = Phase.Streams;
                        break;

                    case Phase.Streams:
                        while (_slot < streams.Count)
                        {
                            int slot = _slot++;
                            if (_slotCount[slot] == 0)
                                continue;
                            if (EncodeStream(log, slot))
                                _written++;
                            if (_slot < streams.Count && System.Diagnostics.Stopwatch.GetTimestamp() > deadline)
                                return false;
                        }
                        FinishChunk();
                        _phase = Phase.Done;
                        _log = null;
                        return true;

                    default:
                        return true;
                }
            }
        }

        /// <summary>The block count, then the HOLDS: their count, then each stream id (from the last) and its end tick (from the chunk's first).</summary>
        void FinishChunk()
        {
            var data = _buffer.data;
            uint count = (uint)_written;
            for (int i = 0; i < 4; i++)
                data[_blocksAt + i] = (byte)(((count >> (7 * i)) & 0x7F) | 0x80);
            data[_blocksAt + 4] = (byte)((count >> 28) & 0x7F);

            _holds.Sort();
            _buffer.WriteVarUInt((ulong)_holds.Count);
            long previous = 0;
            foreach (long hold in _holds)
            {
                long id = hold >> 32;
                ulong end = (ulong)(uint)hold;
                _buffer.WriteVarUInt((ulong)(id - previous));
                _buffer.WriteVarUInt(end);
                previous = id;
            }

            _chunkIndex++;
            if ((_chunkIndex & 63) == 0)
            {
                // Streams that were in no chunk for a while have ended: what they held is forgotten.
                _stale.Clear();
                foreach (var pair in _held)
                    if (pair.Value.chunk < _chunkIndex - 2)
                        _stale.Add(pair.Key);
                foreach (int id in _stale)
                    _held.Remove(id);
            }
        }

        /// <summary>
        /// A per-tick stream all of whose rows in this chunk repeat the row it held through the end of the chunk
        /// before is a HOLD, not a block. Not for streams XORed against another or that others are XORed against.
        /// </summary>
        unsafe bool Holds(in ChunkStream s, int slot, int n, int size, ulong firstTick, int managedBase, int managedFields,
            byte[] logBytes)
        {
            if (!s.forwardFill || s.baseSlot >= 0 || _isBase[slot] || !_held.TryGetValue(s.id, out var last) ||
                last.chunk != _chunkIndex - 1 || last.end + 1 != firstTick || last.row.Length != size)
                return false;
            byte[] rows = _work.data;
            if (size > 0)
            {
                fixed (byte* held = last.row)
                fixed (byte* row = rows)
                    for (int r = 0; r < n; r++)
                        if (UnsafeUtility.MemCmp(row + r * size, held, size) != 0)
                            return false;
            }
            for (int f = 0; f < managedFields; f++)
            {
                var value = last.managed[f];
                for (int r = 0; r < n; r++)
                {
                    int v = managedBase + _entryOf[r] * managedFields + f;
                    if (!SameBytes(logBytes, _valueStart[v], _valueLength[v], value))
                        return false;
                }
            }
            last.chunk = _chunkIndex;
            last.end = firstTick + (ulong)n - 1;
            _holds.Add((long)s.id << 32 | (uint)(last.end - _log.firstTick));
            return true;
        }

        /// <summary>The block's last row and managed values, for the next chunk to compare with.</summary>
        void Remember(in ChunkStream s, int n, int size, ulong firstTick, int managedBase, int managedFields, byte[] logBytes)
        {
            if (!_held.TryGetValue(s.id, out var last) || last.row.Length != size || last.managed.Length != managedFields)
                _held[s.id] = last = new Held { row = new byte[size], managed = new byte[managedFields][] };
            Buffer.BlockCopy(_work.data, (n - 1) * size, last.row, 0, size);
            for (int f = 0; f < managedFields; f++)
            {
                int v = managedBase + _entryOf[n - 1] * managedFields + f;
                if (last.managed[f] == null || !SameBytes(logBytes, _valueStart[v], _valueLength[v], last.managed[f]))
                {
                    var copy = new byte[_valueLength[v]];
                    Buffer.BlockCopy(logBytes, _valueStart[v], copy, 0, copy.Length);
                    last.managed[f] = copy;
                }
            }
            last.chunk = _chunkIndex;
            last.end = firstTick + (ulong)n - 1;
        }

        static unsafe bool SameBytes(byte[] data, int start, int length, byte[] other)
        {
            if (other == null || other.Length != length)
                return false;
            if (length == 0)
                return true;
            fixed (byte* a = &data[start])
            fixed (byte* b = other)
                return UnsafeUtility.MemCmp(a, b, length) == 0;
        }

        bool EncodeStream(ChunkLog log, int slot)
        {
            var buffer = _buffer;
            var work = _work;
            var coded = _coded;
            var keep = _keep;
            byte[] plane = _plane;
            byte[] logBytes = log.bytes.data;

            int entries = _slotCount[slot];
            var s = log.streams[slot];
            int size = s.layout.rowSize;
            int first = _slotFirst[slot];
            int managedFields = s.layout.managed.Count;
            ulong firstTick = _ticks[first];

            int n = entries;
            if (s.forwardFill)
                n = (int)(Math.Max(Math.Min(s.lastTick, log.firstTick + log.tickCount - 1), _ticks[first + entries - 1]) - firstTick) + 1;
            Grow(ref _entryOf, n);
            var entryOf = _entryOf;
            work.Clear();
            work.Ensure(n * size);
            for (int r = 0, e = 0; r < n; r++)
            {
                if (!s.forwardFill)
                    e = r;
                else
                    while (e + 1 < entries && _ticks[first + e + 1] <= firstTick + (ulong)r)
                        e++;
                entryOf[r] = e;
                Buffer.BlockCopy(_rows, _rowOffset[first + e], work.data, r * size, size);
            }
            work.length = n * size;
            int managedBase = _slotManagedFirst[slot];
            if (s.forwardFill)
            {
                if (Holds(s, slot, n, size, firstTick, managedBase, managedFields, logBytes))
                    return false;
                Remember(s, n, size, firstTick, managedBase, managedFields, logBytes);
            }
            uint crc = s.forwardFill ? Crc32(work.data, 0, n * size) : 0;

            // Columns on a grid become integers after the checksum (which is of the rows as recorded) and before
            // the XOR against a base stream (which readers undo before turning them back into floats).
            if (s.layout.hasGrid)
                ToGrid(s.layout, work.data, n);
            if (s.baseSlot >= 0 && _slotCount[s.baseSlot] > 0)
                XorWithBase(s, log.streams[s.baseSlot].layout, first, n, _slotFirst[s.baseSlot], _slotCount[s.baseSlot], work.data);

            keep.Clear();
            for (int r = 0; r < n; r++)
            {
                if (r == 0 || !s.forwardFill ||
                    !SameRow(work.data, r, keep[keep.Count - 1], size, managedBase, managedFields, logBytes))
                    keep.Add(r);
            }
            int k = keep.Count;

            buffer.WriteVarUInt((ulong)s.id);
            buffer.WriteVarUInt((ulong)k);
            ulong previous = log.firstTick;
            foreach (int r in keep)
            {
                ulong tick = s.forwardFill ? firstTick + (ulong)r : _ticks[first + r];
                buffer.WriteVarUInt(tick - previous);
                previous = tick;
            }
            if (s.forwardFill)
                buffer.WriteVarUInt((ulong)(n - 1 - keep[0]));

            int total = k * size;
            if (total > 0)
            {
                coded.Clear();
                coded.Ensure(total);
                for (int i = 0; i < k; i++)
                    Buffer.BlockCopy(work.data, keep[i] * size, coded.data, i * size, size);
                coded.length = total;
                bool keyed = s.layout.keyOffset >= 0;
                if (keyed)
                    KeyRows(s.layout, coded.data, k);
                CodeColumns(s.layout, coded.data, k);

                // Columns on a grid are written after the planes, each as its rows' coded values (zigzag second
                // differences of the integers) in varints: they are small numbers, which planes of four bytes
                // spread over mostly empty bytes. The planes hold the other bytes.
                var gridAt = s.layout.hasGrid ? s.layout.gridAt : null;
                int plain = gridAt != null ? s.layout.plainBytes : size;
                int planes = k * plain;
                if (plane.Length < planes)
                {
                    if (plane.Length > 0)
                        _shelf.Put(plane);
                    plane = _shelf.Take(Mathf.NextPowerOfTwo(Math.Max(1, planes)));
                }
                for (int b = 0, p = 0; b < size; b++)
                {
                    if (gridAt != null && gridAt[b] > 0)
                        continue;
                    for (int r = 0; r < k; r++)
                        plane[p * k + r] = coded.data[r * size + b];
                    p++;
                }
                buffer.WriteBytes(plane, 0, planes);
                if (gridAt != null)
                    foreach (var c in s.layout.columns)
                        if (c.grid > 0)
                            for (int r = 0; r < k; r++)
                                buffer.WriteVarUInt((uint)ReadInt(coded.data, r * size + c.offset));
            }

            bool keyedValues = s.layout.keyOffset >= 0 && k > 0;
            for (int f = 0; f < managedFields; f++)
            {
                int last = -1;
                for (int i = 0; i < k; i++)
                {
                    int r = keep[i];
                    int v = managedBase + entryOf[r] * managedFields + f;
                    if (keyedValues)
                    {
                        // 0: the same as this key's previous row in the chunk.
                        int before = _previousOfKey[i];
                        last = before >= 0 ? managedBase + entryOf[keep[before]] * managedFields + f : -1;
                    }
                    if (last >= 0 && SameValue(logBytes, last, v))
                    {
                        buffer.WriteVarUInt(0);
                        continue;
                    }
                    buffer.WriteVarUInt((ulong)_valueLength[v] + 1);
                    buffer.WriteBytes(logBytes, _valueStart[v], _valueLength[v]);
                    last = v;
                }
            }

            if (s.forwardFill)
            {
                for (int i = 0; i < 4; i++)
                    buffer.WriteByte((byte)(crc >> (8 * i)));
            }

            _plane = plane;
            return true;
        }

        void Layout(List<ChunkStream> streams)
        {
            int slots = streams.Count;
            int rowTotal = 0, rowBytes = 0, managedTotal = 0;
            for (int i = 0; i < slots; i++)
            {
                _slotFirst[i] = rowTotal;
                _slotManagedFirst[i] = managedTotal;
                _slotFill[i] = 0;
                rowTotal += _slotCount[i];
                rowBytes += _slotCount[i] * streams[i].layout.rowSize;
                managedTotal += _slotCount[i] * streams[i].layout.managed.Count;
            }
            Grow(ref _rows, rowBytes);
            Grow(ref _rowOffset, rowTotal);
            Grow(ref _ticks, rowTotal);
            Grow(ref _valueStart, managedTotal);
            Grow(ref _valueLength, managedTotal);

            int offset = 0;
            for (int i = 0; i < slots; i++)
            {
                int size = streams[i].layout.rowSize;
                for (int r = 0; r < _slotCount[i]; r++)
                    _rowOffset[_slotFirst[i] + r] = offset + r * size;
                offset += _slotCount[i] * size;
            }
        }

        int ScatterEntry(byte[] data, int at, List<ChunkStream> streams)
        {
            int header = ReadInt(data, at);
            int slot = header & ~ChunkLog.TickFollows;
            if (header < 0)
            {
                _tick = ReadULong(data, at + 4);
                at += 12;
            }
            else
                at += 4;
            var layout = streams[slot].layout;
            int r = _slotFill[slot]++;
            int row = _slotFirst[slot] + r;
            _ticks[row] = _tick;
            Buffer.BlockCopy(data, at, _rows, _rowOffset[row], layout.rowSize);
            at += layout.rowSize;

            int fields = layout.managed.Count;
            int managedRow = _slotManagedFirst[slot] + r * fields;
            for (int f = 0; f < fields; f++)
            {
                int length = ReadInt(data, at);
                at += 4;
                int v = managedRow + f;
                if (length < 0)
                {
                    _valueStart[v] = _valueStart[v - fields];
                    _valueLength[v] = _valueLength[v - fields];
                    continue;
                }
                _valueStart[v] = at;
                _valueLength[v] = length;
                at += length;
            }
            return at;
        }

        static int SkipEntry(byte[] data, int at, RowLayout layout)
        {
            at += layout.rowSize;
            for (int f = 0; f < layout.managed.Count; f++)
            {
                int length = ReadInt(data, at);
                at += 4 + Math.Max(0, length);
            }
            return at;
        }

        static int ReadInt(byte[] data, int at) =>
            data[at] | data[at + 1] << 8 | data[at + 2] << 16 | data[at + 3] << 24;

        static ulong ReadULong(byte[] data, int at) =>
            (uint)ReadInt(data, at) | (ulong)(uint)ReadInt(data, at + 4) << 32;

        unsafe bool SameValue(byte[] log, int a, int b)
        {
            int length = _valueLength[a];
            if (length != _valueLength[b])
                return false;
            int sa = _valueStart[a], sb = _valueStart[b];
            if (sa == sb)
                return true;
            if (length == 0)
                return true;
            fixed (byte* pa = &log[sa])
            fixed (byte* pb = &log[sb])
                return UnsafeUtility.MemCmp(pa, pb, length) == 0;
        }

        public static unsafe uint Crc32(byte[] data, int offset, int length)
        {
            if ((uint)offset > (uint)data.Length || (uint)length > (uint)(data.Length - offset))
                throw new ArgumentOutOfRangeException(nameof(length));
            uint crc = 0xFFFFFFFFu;
            if (length == 0)
                return 0;
            fixed (byte* start = &data[offset])
            fixed (uint* t = CrcTables)
            {
                byte* p = start;
                for (; length >= 8; length -= 8, p += 8)
                {
                    uint one = (uint)(p[0] | p[1] << 8 | p[2] << 16 | p[3] << 24) ^ crc;
                    crc = t[7 * 256 + (one & 0xFF)] ^ t[6 * 256 + ((one >> 8) & 0xFF)] ^
                          t[5 * 256 + ((one >> 16) & 0xFF)] ^ t[4 * 256 + (one >> 24)] ^
                          t[3 * 256 + p[4]] ^ t[2 * 256 + p[5]] ^ t[256 + p[6]] ^ t[p[7]];
                }
                for (; length > 0; length--, p++)
                    crc = t[(crc ^ *p) & 0xFF] ^ (crc >> 8);
            }
            return crc ^ 0xFFFFFFFFu;
        }

        /// <summary>
        /// A float column on a grid as the integer value * grid (rounded; it already is on the grid). What does not
        /// fit an int, or is not a number, becomes int.MinValue, which readers show as NaN.
        /// </summary>
        static int GridValue(byte[] data, int at, float grid)
        {
            float value = BitConverter.Int32BitsToSingle(ReadInt(data, at));
            double scaled = Math.Round((double)value * grid);
            return Math.Abs(scaled) < 2147483647.0 ? (int)scaled : int.MinValue;
        }

        static void ToGrid(RowLayout layout, byte[] rows, int count)
        {
            int size = layout.rowSize;
            foreach (var c in layout.columns)
            {
                if (c.grid <= 0)
                    continue;
                for (int r = 0, at = c.offset; r < count; r++, at += size)
                {
                    int value = GridValue(rows, at, c.grid);
                    rows[at] = (byte)value;
                    rows[at + 1] = (byte)(value >> 8);
                    rows[at + 2] = (byte)(value >> 16);
                    rows[at + 3] = (byte)(value >> 24);
                }
            }
        }

        void XorWithBase(in ChunkStream s, RowLayout baseLayout, int first, int n, int baseFirst, int baseCount, byte[] rows)
        {
            int size = s.layout.rowSize;
            var baseGrid = baseLayout.hasGrid ? baseLayout.gridAt : null;
            int j = -1;
            for (int r = 0; r < n; r++)
            {
                ulong tick = s.forwardFill ? _ticks[first] + (ulong)r : _ticks[first + r];
                while (j + 1 < baseCount && _ticks[baseFirst + j + 1] <= tick)
                    j++;
                if (j < 0)
                    continue;
                int baseRow = _rowOffset[baseFirst + j];
                for (int c = 0; c < s.baseColumns.Length; c += 2)
                {
                    int own = r * size + s.baseColumns[c], other = baseRow + s.baseColumns[c + 1];
                    float grid = baseGrid != null ? baseGrid[s.baseColumns[c + 1]] : 0f;
                    if (grid > 0)
                    {
                        // Against the base as it is stored: on its grid.
                        int value = GridValue(_rows, other, grid);
                        for (int i = 0; i < 4; i++)
                            rows[own + i] ^= (byte)(value >> (8 * i));
                        continue;
                    }
                    for (int i = 0; i < 4; i++)
                        rows[own + i] ^= _rows[other + i];
                }
            }
        }

        unsafe bool SameRow(byte[] rows, int r, int previous, int size, int managedBase, int managedFields, byte[] log)
        {
            if (size > 0)
            {
                fixed (byte* a = &rows[r * size])
                fixed (byte* b = &rows[previous * size])
                    if (UnsafeUtility.MemCmp(a, b, size) != 0)
                        return false;
            }
            int own = managedBase + _entryOf[r] * managedFields, other = managedBase + _entryOf[previous] * managedFields;
            for (int f = 0; own != other && f < managedFields; f++)
                if (!SameValue(log, own + f, other + f))
                    return false;
            return true;
        }

        /// <summary>
        /// KEYED rows: from <see cref="RowLayout.keyedFrom"/> on, each row is XORed with the previous row of the same
        /// key (read from the key column, which is left as it is); the first row of a key in the chunk stays whole.
        /// Also notes each row's previous row of its key, for the managed values.
        /// </summary>
        void KeyRows(RowLayout layout, byte[] rows, int count)
        {
            int size = layout.rowSize, from = layout.keyedFrom, key = layout.keyOffset;
            Grow(ref _previousOfKey, count);
            _lastOfKey.Clear();
            for (int r = 0; r < count; r++)
            {
                int id = ReadInt(rows, r * size + key);
                _previousOfKey[r] = _lastOfKey.TryGetValue(id, out int before) ? before : -1;
                _lastOfKey[id] = r;
            }
            // From the last row back, so every row is XORed with its key's previous row as it was.
            for (int r = count - 1; r > 0; r--)
            {
                int before = _previousOfKey[r];
                if (before < 0)
                    continue;
                int at = r * size, other = before * size;
                for (int b = from; b < size; b++)
                    if (b < key || b >= key + 4)
                        rows[at + b] ^= rows[other + b];
            }
        }

        void CodeColumns(RowLayout layout, byte[] rows, int count)
        {
            int size = layout.rowSize;
            var isNumeric = NumericMask(layout);
            // Keyed columns were coded against their key's previous row already.
            int end = layout.keyedFrom >= 0 ? layout.keyedFrom : size;

            for (int r = count - 1; r > 0; r--)
            {
                int at = r * size, prev = at - size;
                for (int b = 0; b < end; b++)
                    if (!isNumeric[b])
                        rows[at + b] ^= rows[prev + b];
            }

            foreach (var c in layout.columns)
            {
                if (c.offset >= end)
                    continue;
                if (c.size == 4 && IsWideNumeric(c.type))
                {
                    uint prev = 0, prevDelta = 0;
                    for (int r = 0; r < count; r++)
                    {
                        int at = r * size + c.offset;
                        uint v = (uint)(rows[at] | rows[at + 1] << 8 | rows[at + 2] << 16 | rows[at + 3] << 24);
                        uint delta = v - prev, dd = delta - prevDelta;
                        prev = v;
                        prevDelta = delta;
                        uint z = (dd << 1) ^ (uint)((int)dd >> 31);
                        rows[at] = (byte)z;
                        rows[at + 1] = (byte)(z >> 8);
                        rows[at + 2] = (byte)(z >> 16);
                        rows[at + 3] = (byte)(z >> 24);
                    }
                }
                else if (c.size == 8 && IsWideNumeric(c.type))
                {
                    ulong prev = 0, prevDelta = 0;
                    for (int r = 0; r < count; r++)
                    {
                        int at = r * size + c.offset;
                        ulong v = 0;
                        for (int i = 0; i < 8; i++)
                            v |= (ulong)rows[at + i] << (8 * i);
                        ulong delta = v - prev, dd = delta - prevDelta;
                        prev = v;
                        prevDelta = delta;
                        ulong z = (dd << 1) ^ (ulong)((long)dd >> 63);
                        for (int i = 0; i < 8; i++)
                            rows[at + i] = (byte)(z >> (8 * i));
                    }
                }
            }
        }

        static bool IsWideNumeric(string type) =>
            type == "f32" || type == "i32" || type == "u32" || type == "f64" || type == "i64" || type == "u64";

        bool[] NumericMask(RowLayout layout)
        {
            if (_numericMasks.TryGetValue(layout, out var mask))
                return mask;
            mask = new bool[layout.rowSize];
            foreach (var c in layout.columns)
                if ((c.size == 4 || c.size == 8) && IsWideNumeric(c.type))
                    for (int i = 0; i < c.size; i++)
                        mask[c.offset + i] = true;
            _numericMasks[layout] = mask;
            return mask;
        }
    }
}
#endif
