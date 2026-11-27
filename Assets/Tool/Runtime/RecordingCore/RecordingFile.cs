#if PURRNET_RECORDING_HOST
#if UNITY_WEBGL && !UNITY_EDITOR
#define PURRREC_WEB
#endif
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Unity.Profiling;
using UnityEngine;

namespace PurrNet.Recording
{
    public enum RecordKind : byte
    {
        Header = 1,
        Type = 2,
        Stream = 3,
        Entity = 4,
        Despawn = 5,
        Chunk = 6,
        Colliders = 7,
        Player = 8,
        Footer = 9,
        Mesh = 10,
        Batch = 11,
        Attach = 12,
        Detach = 13,
        Part = 14,
        Owner = 15,
        Observers = 16,
        Renderers = 17,
        Effects = 18,
        Terrain = 19,
        TerrainMaterials = 20,
        /// <summary>
        /// A terrain like <see cref="Terrain"/>, its heights as the 16-bit samples Unity keeps them in: after the size,
        /// a tolerance (varint, in steps: 0 is exact), then each sample minus its prediction from the samples left
        /// of, above and above-left of it, wrapped to 16 bits and zigzagged (small differences either way become small
        /// numbers), as a plane of low bytes and then a plane of high bytes (see <see cref="TerrainHeights"/>).
        /// </summary>
        TerrainHeights = 21,
        /// <summary>
        /// A mesh like <see cref="Mesh"/>, packed (see <see cref="MeshPacking"/>): each vertex axis as a 16-bit step
        /// of the mesh's bounds (an error of at most 1/131070 of the mesh's size), the indices as deltas.
        /// </summary>
        MeshPacked = 22
    }

    /// <summary>
    /// Packs a MESH payload (id, name, vertex count, float vertices, index count, uint indices) as MESH_PACKED: the
    /// id and name as they are; the vertex count; the bounds' minimum and extent (3 + 3 floats); for x, y then z, each
    /// vertex as round((v - min) / extent * 65535), zigzag delta from the previous vertex's (16 bits), a plane of low
    /// bytes then one of high bytes; the index count; each index as the zigzag delta from the previous (32 bits), in
    /// four byte planes. Deltas in planes are what makes the difference: a mesh's floats barely compress.
    /// </summary>
    static class MeshPacking
    {
        /// <summary>More than the packed form of a payload of <paramref name="length"/> bytes can take.</summary>
        public static int MaxBytes(int length) => length + 32;

        /// <summary>Like the pointer overload, from and to arrays.</summary>
        public static unsafe int Pack(byte[] payload, int length, byte[] output)
        {
            if (length > payload.Length || output.Length < MaxBytes(length))
                throw new ArgumentOutOfRangeException(nameof(output));
            fixed (byte* source = payload)
            fixed (byte* target = output)
                return Pack(source, length, target);
        }

        static unsafe bool ReadVarUInt(byte* data, int length, ref int at, out uint value)
        {
            value = 0;
            for (int shift = 0; shift < 35; shift += 7)
            {
                if (at >= length)
                    return false;
                byte b = data[at++];
                value |= (uint)(b & 0x7F) << shift;
                if (b < 0x80)
                    return true;
            }
            return false;
        }

        static unsafe int WriteVarUInt(byte* output, int at, uint value)
        {
            while (value >= 0x80)
            {
                output[at++] = (byte)(value | 0x80);
                value >>= 7;
            }
            output[at++] = (byte)value;
            return at;
        }

        /// <summary>
        /// Writes the packed form to <paramref name="output"/> (room for <see cref="MaxBytes"/>) and returns its
        /// length, or -1 when the payload is not a mesh that packs (vertices that are not finite).
        /// </summary>
        public static unsafe int Pack(byte* payload, int length, byte* output)
        {
            int at = 0;
            if (!ReadVarUInt(payload, length, ref at, out _) || !ReadVarUInt(payload, length, ref at, out uint nameLength) ||
                nameLength > length - at)
                return -1;
            at += (int)nameLength;
            int prefix = at;
            if (!ReadVarUInt(payload, length, ref at, out uint vertexCount) || (long)vertexCount * 12 > length - at)
                return -1;
            float* vertices = (float*)(payload + at);
            at += (int)vertexCount * 12;
            if (!ReadVarUInt(payload, length, ref at, out uint indexCount) || (long)indexCount * 4 != length - at)
                return -1;
            byte* indices = payload + at;
            int n = (int)vertexCount, m = (int)indexCount;

            float* min = stackalloc float[3];
            float* extent = stackalloc float[3];
            for (int a = 0; a < 3; a++)
            {
                float lo = float.PositiveInfinity, hi = float.NegativeInfinity;
                for (int v = 0; v < n; v++)
                {
                    float x = vertices[v * 3 + a];
                    if (float.IsNaN(x) || float.IsInfinity(x))
                        return -1;
                    if (x < lo) lo = x;
                    if (x > hi) hi = x;
                }
                if (n == 0)
                    lo = hi = 0f;
                min[a] = lo;
                extent[a] = hi - lo;
                if (float.IsInfinity(extent[a]))
                    return -1;
            }

            Buffer.MemoryCopy(payload, output, prefix, prefix);
            int o = WriteVarUInt(output, prefix, vertexCount);
            Buffer.MemoryCopy(min, output + o, 12, 12);
            o += 12;
            Buffer.MemoryCopy(extent, output + o, 12, 12);
            o += 12;
            for (int a = 0; a < 3; a++)
            {
                byte* low = output + o, high = output + o + n;
                double lo = min[a], e = extent[a];
                int previous = 0;
                for (int v = 0; v < n; v++)
                {
                    int q = e > 0 ? (int)Math.Round((vertices[v * 3 + a] - lo) / e * 65535.0) : 0;
                    q = q < 0 ? 0 : q > 65535 ? 65535 : q;
                    short delta = (short)(q - previous);
                    ushort z = (ushort)((delta << 1) ^ (delta >> 15));
                    low[v] = (byte)z;
                    high[v] = (byte)(z >> 8);
                    previous = q;
                }
                o += 2 * n;
            }
            o = WriteVarUInt(output, o, indexCount);
            int last = 0;
            for (int i = 0; i < m; i++)
            {
                int index = indices[i * 4] | indices[i * 4 + 1] << 8 | indices[i * 4 + 2] << 16 | indices[i * 4 + 3] << 24;
                int delta = unchecked(index - last);
                uint z = (uint)((delta << 1) ^ (delta >> 31));
                for (int b = 0; b < 4; b++)
                    output[o + b * m + i] = (byte)(z >> (8 * b));
                last = index;
            }
            return o + 4 * m;
        }
    }

    /// <summary>
    /// Packs a terrain's normalized heights as 16-bit samples for <see cref="RecordKind.TerrainHeights"/>. Unity
    /// stores a heightmap as 16-bit values and hands them out as <c>sample * (1f / 32766f)</c>, so every height a
    /// terrain gives back packs exactly; one that does not (written some other way) keeps the float record.
    /// </summary>
    static class TerrainHeights
    {
        const float Step = 1f / 32766f;

        /// <summary>The most bytes the packed heights of <paramref name="slices"/> take (the tolerance, then two planes).</summary>
        public static int PackedBytes(List<float[,]> slices)
        {
            int samples = 0;
            foreach (var slice in slices)
                samples += slice.Length;
            return 5 + samples * 2;
        }

        /// <summary>Like the pointer overload, into <paramref name="output"/> from <paramref name="offset"/>.</summary>
        public static unsafe int TryPack(List<float[,]> slices, byte[] output, int offset, int tolerance = 0)
        {
            if (offset < 0 || output.Length - offset < PackedBytes(slices))
                throw new ArgumentOutOfRangeException(nameof(offset));
            fixed (byte* start = output)
                return TryPack(slices, start + offset, tolerance);
        }

        /// <summary>
        /// Writes the tolerance (a varint: how many 16-bit steps a height may be off, 0 for exact) and the two planes
        /// to <paramref name="output"/> (room for <see cref="PackedBytes"/>), and returns how many bytes that took, or
        /// -1 if a height is not one Unity could have given back. With a tolerance, each residual is rounded to a
        /// multiple of 2 * tolerance + 1 steps and the predictions use the heights as they will be read back (kept
        /// within 0..65535), so no height is ever off by more than the tolerance. Terrain is smooth: its residuals
        /// are mostly 0 or 1 step, the noise of rounding a smooth surface, which a tolerance takes out.
        /// </summary>
        public static unsafe int TryPack(List<float[,]> slices, byte* output, int tolerance = 0)
        {
            if (slices.Count == 0 || tolerance < 0)
                return -1;
            int side = slices[0].GetLength(1);
            int count = 0;
            foreach (var slice in slices)
            {
                if (slice.GetLength(1) != side)
                    return -1;
                count += slice.GetLength(0);
            }
            if (side < 2 || count != side)
                return -1;

            int samples = side * side;
            int o = 0;
            for (uint t = (uint)tolerance; ; t >>= 7)
            {
                if (t < 0x80)
                {
                    output[o++] = (byte)t;
                    break;
                }
                output[o++] = (byte)(t | 0x80);
            }
            int step = 2 * tolerance + 1;
            byte* low = output + o, high = output + o + samples;
            var previous = new int[side];
            var current = new int[side];
            int z = 0;
            foreach (var slice in slices)
            {
                int rows = slice.GetLength(0);
                if (rows == 0)
                    continue;
                fixed (float* start = &slice[0, 0])
                {
                    for (int r = 0; r < rows; r++, z++)
                    {
                        float* row = start + r * side;
                        int at = z * side;
                        for (int x = 0; x < side; x++)
                        {
                            float height = row[x];
                            double scaled = height * 32766.0;
                            if (!(scaled >= 0 && scaled <= 65535))
                                return -1;
                            int sample = (int)Math.Round(scaled);
                            // The cast rounds to a float: the runtime may hold float arithmetic in double precision,
                            // and the product must be compared exactly as Unity stores it.
                            float back = (float)((float)sample * Step);
                            if (back != height)
                                return -1;
                            int predicted = z == 0
                                ? (x == 0 ? 0 : current[x - 1])
                                : x == 0 ? previous[0] : current[x - 1] + previous[x] - previous[x - 1];
                            int difference = sample - predicted;
                            if (tolerance > 0)
                            {
                                // Rounded to a multiple of the step; the height read back is what predicts the next.
                                int q = difference >= 0 ? (difference + tolerance) / step : -((tolerance - difference) / step);
                                int read = predicted + q * step;
                                current[x] = read < 0 ? 0 : read > 65535 ? 65535 : read;
                                difference = q;
                            }
                            else
                                current[x] = sample;
                            short residual = unchecked((short)difference);
                            ushort zigzag = (ushort)((residual << 1) ^ (residual >> 15));
                            low[at + x] = (byte)zigzag;
                            high[at + x] = (byte)(zigzag >> 8);
                        }
                        (previous, current) = (current, previous);
                    }
                }
            }
            return o + 2 * samples;
        }
    }

    interface IStepwiseRecordProducer : IRecordProducer
    {
        void Begin();

        bool Step(long deadline);
    }

    public interface IRecordProducer
    {
        RecordingBuffer Produce();

        void Release();
    }

    public sealed class RecordingFile : IDisposable
    {
        struct PendingRecord
        {
            public RecordKind kind;
            public byte[] payload;
            public IRecordProducer producer;
            public bool compress;
            public IntPtr native;
            public int nativeLength;
#if !PURRREC_WEB
            public List<float[,]> terrainHeights;
            public byte[] terrainHoles;
            public int terrainTolerance;
#endif
        }

        readonly Queue<PendingRecord> _queue = new Queue<PendingRecord>(64);
        long _rawBytes;
#if !PURRREC_WEB
        long _pendingTerrainBytes;
#endif
        volatile Exception _error;

        /// <summary>
        /// What went wrong writing the file (the disk is full, the file was taken away), or null. Nothing more is
        /// written after it: records are dropped as they come.
        /// </summary>
        public Exception error => _error;
        volatile bool _busy;
        volatile bool _closed;

        /// <summary>Everything that was added is in the file and the file is closed: it can be read, sent or deleted.</summary>
        public bool closed => _closed;
        long _produceTicks, _maxRecordTicks;
#if !PURRREC_WEB
        bool _completed;
        readonly Thread _thread;
        Thread _after;
        FileStream _stream;
        long _bytesWritten, _compressTicks;
        readonly BufferStream _compressed = new BufferStream(new RecordingBuffer(ByteShelf.shared, 1 << 16));
        GCHandle _compressedHandle;
        readonly NativeSink _nativeSink = new NativeSink();
        GCHandle _nativeSinkHandle;
        readonly byte[] _slice = new byte[1 << 16];
#endif

        public double pumpBudgetMs { get; set; } = 1.0;

        public double produceMs => Interlocked.Read(ref _produceTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        public double maxRecordMs => Interlocked.Read(ref _maxRecordTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
#if PURRREC_WEB
        public double compressMs => WebRecording.Stat(_web, WebRecording.StatCompressMs);
        public bool nativeDeflate => _web != 0 && WebRecording.Stat(_web, WebRecording.StatDeflate) != 0;
        public long bytesWritten => (long)WebRecording.Stat(_web, WebRecording.StatBytes);
#else
        public double compressMs => Interlocked.Read(ref _compressTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        public bool nativeDeflate { get; private set; } = true;
        public long bytesWritten => Interlocked.Read(ref _bytesWritten);
#endif

        public bool busy
        {
            get
            {
                lock (_queue)
                    return _busy || _queue.Count > 0;
            }
        }

        public string path { get; }

#if PURRREC_WEB
        public static bool encodesOnMainThread => true;
        internal long pendingTerrainBytes => 0;
#else
        public static bool encodesOnMainThread => false;
        internal long pendingTerrainBytes => Interlocked.Read(ref _pendingTerrainBytes);
#endif
        public long rawBytes => Interlocked.Read(ref _rawBytes);

#if PURRREC_WEB
        readonly int _web;
        readonly RecordingBuffer _header = new RecordingBuffer(16);

        public RecordingFile(string path, long maxLocalBytes, RecordingFile after = null)
        {
            this.path = path;
            _web = WebRecording.Open(Path.GetFileName(path), maxLocalBytes);
            if (_web == 0)
                return;
            var magic = new RecordingBuffer(16);
            magic.WriteBytes(Encoding.ASCII.GetBytes("PURRREC\0"), 0, 8);
            magic.WriteByte(1);
            magic.WriteByte(0);
            WebRecording.Append(_web, -1, false, magic.data, magic.length);
        }

        bool _stepping;

        public void Pump()
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            long deadline = start + (long)(pumpBudgetMs * System.Diagnostics.Stopwatch.Frequency / 1000.0);
            while (_queue.Count > 0)
            {
                if (_error == null && _queue.Peek().producer is IStepwiseRecordProducer stepwise)
                {
                    if (!_stepping)
                    {
                        stepwise.Begin();
                        _stepping = true;
                    }
                    if (!stepwise.Step(deadline))
                        break;
                    _stepping = false;
                }
                WriteRecord(_queue.Dequeue(), _header);
                if (System.Diagnostics.Stopwatch.GetTimestamp() > deadline)
                    break;
            }
            long spent = System.Diagnostics.Stopwatch.GetTimestamp() - start;
            _produceTicks += spent;
            if (spent > _maxRecordTicks)
                _maxRecordTicks = spent;
        }
#else
        public void Pump() { }

        /// <summary>
        /// The file is created, written and closed by a thread of its own: the caller never waits for the disk.
        /// <paramref name="after"/> is a file that was completed but may not be closed yet. This one's thread
        /// starts where that one's ends, so the two never work at the same time.
        /// </summary>
        public RecordingFile(string path, long maxLocalBytes, RecordingFile after = null)
        {
            this.path = path;
            _after = after?._thread;
            _compressedHandle = GCHandle.Alloc(_compressed.buffer);
            _nativeSinkHandle = GCHandle.Alloc(_nativeSink);
            _thread = new Thread(WriteLoop) { IsBackground = true, Name = "PurrDiction recording writer", Priority = System.Threading.ThreadPriority.BelowNormal };
            _thread.Start();
        }

        void WriteLoop()
        {
            UnityEngine.Profiling.Profiler.BeginThreadProfiling("PurrDiction", "Recording writer");
            try
            {
                _after?.Join();
                _after = null;
                Open();
                var header = new RecordingBuffer(16);
                while (Take(out var record))
                {
                    WriteRecord(record, header);
                    bool idle;
                    lock (_queue)
                        idle = _queue.Count == 0;
                    if (idle)
                        Flush();
                }
                Flush();
            }
            finally
            {
                Close();
                UnityEngine.Profiling.Profiler.EndThreadProfiling();
            }
        }

        void Open()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
                _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
                var magic = Encoding.ASCII.GetBytes("PURRREC\0");
                _stream.Write(magic, 0, magic.Length);
                _stream.WriteByte(1);
                _stream.WriteByte(0);
                Interlocked.Add(ref _bytesWritten, 10);
            }
            catch (Exception e)
            {
                _error = e;
            }
        }

        void Close()
        {
            if (_compressedHandle.IsAllocated)
                _compressedHandle.Free();
            if (_nativeSinkHandle.IsAllocated)
                _nativeSinkHandle.Free();
            _nativeSink.Free();
            try
            {
                _stream?.Dispose();
            }
            catch (Exception e)
            {
                _error ??= e;
            }
            _closed = true;
        }

        void Flush()
        {
            if (_error != null)
                return;
            try
            {
                _stream.Flush();
            }
            catch (Exception e)
            {
                _error = e;
            }
        }

        bool Take(out PendingRecord record)
        {
            // Release the previous snapshot before the writer blocks on an empty queue.
            record = default;
            lock (_queue)
            {
                while (_queue.Count == 0)
                {
                    if (_completed)
                        return false;
                    Monitor.Wait(_queue);
                }
                record = _queue.Dequeue();
                _busy = true;
                return true;
            }
        }
#endif

        public RecordingFile(string path) : this(path, 0) { }

        public void SetLink(string json)
        {
#if PURRREC_WEB
            if (_web != 0)
                WebRecording.Link(_web, json);
#endif
        }

        public void WriteJson(RecordKind kind, string json, bool compress = false)
        {
            Enqueue(kind, Encoding.UTF8.GetBytes(json), compress);
        }

        public void ReserveOutput(int bytes)
        {
#if !PURRREC_WEB
            _compressed.buffer.Reserve(bytes);
#endif
        }

        public void Enqueue(RecordKind kind, byte[] payload, bool compress)
        {
            if (_error != null)
                return;
            Interlocked.Add(ref _rawBytes, payload.Length);
            Add(new PendingRecord { kind = kind, payload = payload, compress = compress });
        }

        public void Enqueue(RecordKind kind, IRecordProducer producer, bool compress)
        {
            if (_error != null)
            {
                producer.Release();
                return;
            }
            Add(new PendingRecord { kind = kind, producer = producer, compress = compress });
        }

        /// <summary>
        /// A record whose payload is native memory (from UnsafeUtility.Malloc, Allocator.Persistent): the file takes
        /// it over, compresses and writes it from where it is, and frees it. For payloads of megabytes (meshes),
        /// which must not pass through the managed heap.
        /// </summary>
        public unsafe void EnqueueNative(RecordKind kind, IntPtr data, int length, bool compress)
        {
#if PURRREC_WEB
            var bytes = new byte[length];
            Marshal.Copy(data, bytes, 0, length);
            Unity.Collections.LowLevel.Unsafe.UnsafeUtility.Free((void*)data, Unity.Collections.Allocator.Persistent);
            Enqueue(kind, bytes, compress);
#else
            if (_error != null)
            {
                Unity.Collections.LowLevel.Unsafe.UnsafeUtility.Free((void*)data, Unity.Collections.Allocator.Persistent);
                return;
            }
            Interlocked.Add(ref _rawBytes, length);
            Add(new PendingRecord { kind = kind, native = data, nativeLength = length, compress = compress });
#endif
        }

        /// <summary>
        /// A terrain: <paramref name="tolerance"/> is how many 16-bit steps its heights may be off when they are packed
        /// (see <see cref="TerrainHeights"/>), 0 for exact.
        /// </summary>
        internal void EnqueueTerrain(RecordingBuffer prefix, List<float[,]> heights, byte[] holes, int tolerance = 0)
        {
            if (_error != null)
                return;
            int heightBytes = 0;
            foreach (var rows in heights)
                heightBytes += rows.Length * sizeof(float);
            int holeBytes = holes?.Length ?? 0;
            int length = prefix.length + heightBytes + holeBytes;
#if PURRREC_WEB
            var packed = new byte[prefix.length + TerrainHeights.PackedBytes(heights) + holeBytes];
            int packedBytes = TerrainHeights.TryPack(heights, packed, prefix.length, tolerance);
            if (packedBytes >= 0)
            {
                Buffer.BlockCopy(prefix.data, 0, packed, 0, prefix.length);
                if (holeBytes != 0)
                    Buffer.BlockCopy(holes, 0, packed, prefix.length + packedBytes, holeBytes);
                int total = prefix.length + packedBytes + holeBytes;
                if (total != packed.Length)
                    Array.Resize(ref packed, total);
                Enqueue(RecordKind.TerrainHeights, packed, true);
                return;
            }
            var bytes = new byte[length];
            Buffer.BlockCopy(prefix.data, 0, bytes, 0, prefix.length);
            int at = prefix.length;
            foreach (var rows in heights)
            {
                int rowBytes = rows.Length * sizeof(float);
                Buffer.BlockCopy(rows, 0, bytes, at, rowBytes);
                at += rowBytes;
            }
            if (holeBytes != 0)
                Buffer.BlockCopy(holes, 0, bytes, prefix.length + heightBytes, holeBytes);
            Enqueue(RecordKind.Terrain, bytes, true);
#else
            // The writer reads Unity's height snapshot directly. Only the small shared prefix needs a copy.
            Interlocked.Add(ref _rawBytes, length);
            var record = new PendingRecord
            {
                kind = RecordKind.Terrain, payload = prefix.ToArray(), compress = true,
                terrainHeights = heights, terrainHoles = holes, nativeLength = heightBytes, terrainTolerance = tolerance
            };
            Interlocked.Add(ref _pendingTerrainBytes, heightBytes);
            Add(record);
#endif
        }

#if !PURRREC_WEB
        unsafe void WriteNative(RecordKind recordKind, ReadOnlySpan<byte> payload, bool compress, RecordingBuffer header,
            List<float[,]> heights = null, int heightBytes = 0, ReadOnlySpan<byte> holes = default)
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            int length = payload.Length + heightBytes + holes.Length;
            byte kind = (byte)recordKind;
            if (compress)
            {
                using var _ = CompressMarker.Auto();
                // Terrain grids can compress to a tiny fraction of their input, especially flat tiles.
                int reserve = length / 2 + 1024;
                bool terrain = recordKind == RecordKind.Terrain || recordKind == RecordKind.TerrainHeights;
                if (terrain)
                    reserve = Math.Min(reserve, 64 * 1024);
                _nativeSink.Begin(reserve);
                bool compressed = NativeDeflate.TryCompress(payload, GCHandle.ToIntPtr(_nativeSinkHandle), heights, holes) && !_nativeSink.failed;
                if (!compressed && terrain)
                {
                    // The native Mono compressor is not present in all players. Stream from the same input
                    // through the reusable file slice if we have to use managed DEFLATE.
                    nativeDeflate = false;
                    _nativeSink.Begin(reserve);
                    using (var deflate = new DeflateStream(_nativeSink, System.IO.Compression.CompressionLevel.Fastest, true))
                    {
                        WriteSlices(deflate, payload);
                        WriteTerrainRows(deflate, heights);
                        WriteSlices(deflate, holes);
                    }
                    compressed = !_nativeSink.failed;
                }
                if (compressed && (!terrain || _nativeSink.length < length))
                {
                    length = _nativeSink.length;
                    payload = new ReadOnlySpan<byte>(_nativeSink.data, length);
                    heights = null;
                    holes = default;
                    kind |= 0x80;
                }
            }
            long compressedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            Interlocked.Add(ref _compressTicks, compressedAt - started);
            if (compressedAt - started > Interlocked.Read(ref _maxRecordTicks))
                Interlocked.Exchange(ref _maxRecordTicks, compressedAt - started);

            using var write = WriteMarker.Auto();
            header.Clear();
            header.WriteByte(kind);
            header.WriteVarUInt((ulong)length);
            _stream.Write(header.data, 0, header.length);
            WriteSlices(_stream, payload);
            WriteTerrainRows(_stream, heights);
            WriteSlices(_stream, holes);
            Interlocked.Add(ref _bytesWritten, header.length + length);
        }

        /// <summary>
        /// The heights as 16-bit samples (<see cref="RecordKind.TerrainHeights"/>) when they pack exactly, packed into
        /// native memory next to the prefix; otherwise as floats, read straight from the snapshot.
        /// </summary>
        unsafe void WriteTerrain(in PendingRecord record, RecordingBuffer header)
        {
            int prefixLength = record.payload.Length;
            long packedLength = prefixLength + (long)TerrainHeights.PackedBytes(record.terrainHeights);
            if (packedLength <= int.MaxValue)
            {
                var packed = (byte*)Unity.Collections.LowLevel.Unsafe.UnsafeUtility.Malloc(packedLength, 1,
                    Unity.Collections.Allocator.Persistent);
                try
                {
                    record.payload.AsSpan().CopyTo(new Span<byte>(packed, prefixLength));
                    int written = TerrainHeights.TryPack(record.terrainHeights, packed + prefixLength, record.terrainTolerance);
                    if (written >= 0)
                    {
                        WriteNative(RecordKind.TerrainHeights, new ReadOnlySpan<byte>(packed, prefixLength + written), true, header,
                            holes: record.terrainHoles);
                        return;
                    }
                }
                finally
                {
                    Unity.Collections.LowLevel.Unsafe.UnsafeUtility.Free(packed, Unity.Collections.Allocator.Persistent);
                }
            }
            WriteNative(record.kind, record.payload, true, header, record.terrainHeights, record.nativeLength, record.terrainHoles);
        }

        /// <summary>A mesh packed in native memory (see <see cref="MeshPacking"/>), or false to write it as it is.</summary>
        unsafe bool WritePackedMesh(byte* payload, int length, bool compress, RecordingBuffer header)
        {
            var packed = (byte*)Unity.Collections.LowLevel.Unsafe.UnsafeUtility.Malloc(MeshPacking.MaxBytes(length), 1,
                Unity.Collections.Allocator.Persistent);
            try
            {
                int packedLength = MeshPacking.Pack(payload, length, packed);
                if (packedLength < 0)
                    return false;
                WriteNative(RecordKind.MeshPacked, new ReadOnlySpan<byte>(packed, packedLength), compress, header);
                return true;
            }
            finally
            {
                Unity.Collections.LowLevel.Unsafe.UnsafeUtility.Free(packed, Unity.Collections.Allocator.Persistent);
            }
        }

        unsafe void WriteTerrainRows(Stream stream, List<float[,]> heights)
        {
            if (heights == null)
                return;
            foreach (var rows in heights)
                fixed (float* samples = &rows[0, 0])
                    WriteSlices(stream, new ReadOnlySpan<byte>(samples, rows.Length * sizeof(float)));
        }

        void WriteSlices(Stream stream, ReadOnlySpan<byte> source)
        {
            while (!source.IsEmpty)
            {
                int n = Math.Min(_slice.Length, source.Length);
                source.Slice(0, n).CopyTo(_slice);
                stream.Write(_slice, 0, n);
                source = source.Slice(n);
            }
        }
#endif

        static readonly ProfilerMarker EncodeMarker = new ProfilerMarker("Recording.Encode");
        static readonly ProfilerMarker CompressMarker = new ProfilerMarker("Recording.Compress");
        static readonly ProfilerMarker WriteMarker = new ProfilerMarker("Recording.Write");

        void Add(in PendingRecord record)
        {
            lock (_queue)
            {
                _queue.Enqueue(record);
                Monitor.Pulse(_queue);
            }
        }

        unsafe void WriteRecord(in PendingRecord record, RecordingBuffer header)
        {
            if (_error != null)
            {
                record.producer?.Release();
                ReleasePayload(record);
                return;
            }
            _busy = true;
            try
            {
#if !PURRREC_WEB
                if (record.native != IntPtr.Zero)
                {
                    if (record.kind == RecordKind.Mesh && WritePackedMesh((byte*)record.native, record.nativeLength, record.compress, header))
                        return;
                    WriteNative(record.kind, new ReadOnlySpan<byte>((void*)record.native, record.nativeLength), record.compress, header);
                    return;
                }
                if (record.terrainHeights != null)
                {
                    WriteTerrain(record, header);
                    return;
                }
#endif
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                byte[] payload = record.payload;
                int length = payload?.Length ?? 0;
                if (record.producer != null)
                {
                    using var _ = EncodeMarker.Auto();
                    var produced = record.producer.Produce();
                    payload = produced.data;
                    length = produced.length;
                    Interlocked.Add(ref _rawBytes, length);
                }
#if PURRREC_WEB
                if (_web != 0)
                    WebRecording.Append(_web, (int)record.kind, record.compress, payload, length);
#else
                long encoded = System.Diagnostics.Stopwatch.GetTimestamp();
                Interlocked.Add(ref _produceTicks, encoded - started);
                byte kind = (byte)record.kind;
                if (record.compress)
                {
                    using var _ = CompressMarker.Auto();
                    _compressed.buffer.Clear();
                    if (!NativeDeflate.TryCompress(payload.AsSpan(0, length), GCHandle.ToIntPtr(_compressedHandle)))
                    {
                        nativeDeflate = false;
                        _compressed.buffer.Clear();
                        using (var deflate = new DeflateStream(_compressed, System.IO.Compression.CompressionLevel.Optimal, true))
                            deflate.Write(payload, 0, length);
                    }
                    payload = _compressed.buffer.data;
                    length = _compressed.buffer.length;
                    kind |= 0x80;
                }
                long compressed = System.Diagnostics.Stopwatch.GetTimestamp();
                Interlocked.Add(ref _compressTicks, compressed - encoded);
                if (compressed - started > Interlocked.Read(ref _maxRecordTicks))
                    Interlocked.Exchange(ref _maxRecordTicks, compressed - started);

                using var write = WriteMarker.Auto();
                header.Clear();
                header.WriteByte(kind);
                header.WriteVarUInt((ulong)length);
                _stream.Write(header.data, 0, header.length);
                _stream.Write(payload, 0, length);
                Interlocked.Add(ref _bytesWritten, header.length + length);
#endif
            }
            catch (Exception e)
            {
                _error = e;
            }
            finally
            {
#if !PURRREC_WEB
                if (record.native != IntPtr.Zero || record.terrainHeights != null)
                    _nativeSink.Free();
#endif
                record.producer?.Release();
                ReleasePayload(record);
                _busy = false;
            }
        }

        unsafe void ReleasePayload(in PendingRecord record)
        {
#if !PURRREC_WEB
            if (record.terrainHeights != null)
                Interlocked.Add(ref _pendingTerrainBytes, -record.nativeLength);
#endif
            if (record.native != IntPtr.Zero)
                Unity.Collections.LowLevel.Unsafe.UnsafeUtility.Free((void*)record.native, Unity.Collections.Allocator.Persistent);
        }

        /// <summary>
        /// Nothing more will be added. The writer thread writes what is queued and closes the file, and
        /// <see cref="closed"/> says when: the caller does not wait. Without threads (WebGL) it is done at once.
        /// </summary>
        public void Complete()
        {
#if PURRREC_WEB
            Dispose();
#else
            lock (_queue)
            {
                _completed = true;
                Monitor.PulseAll(_queue);
            }
#endif
        }

        /// <summary>Completes the file and waits until it is closed.</summary>
        public void Dispose()
        {
#if PURRREC_WEB
            if (_closed)
                return;
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            while (_queue.Count > 0)
                WriteRecord(_queue.Dequeue(), _header);
            _produceTicks += System.Diagnostics.Stopwatch.GetTimestamp() - start;
            if (_web != 0)
                WebRecording.Close(_web);
            _closed = true;
#else
            Complete();
            _thread.Join();
#endif
        }
    }

    sealed class BufferStream : Stream
    {
        public readonly RecordingBuffer buffer;

        public BufferStream(RecordingBuffer buffer) => this.buffer = buffer;

        public override void Write(byte[] source, int offset, int count) => buffer.WriteBytes(source, offset, count);
        public override void WriteByte(byte value) => buffer.WriteByte(value);
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => buffer.length;
        public override long Position
        {
            get => buffer.length;
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override int Read(byte[] destination, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

#if !PURRREC_WEB
    /// <summary>Compressed output that grows in native memory, for records too large to pass through the managed heap.</summary>
    sealed unsafe class NativeSink : Stream
    {
        public byte* data;
        public int length, capacity;
        public bool failed;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => length;
        public override long Position { get => length; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            fixed (byte* source = buffer.AsSpan(offset, count))
                Write(source, count);
        }

        public void Begin(int reserve)
        {
            Free();
            capacity = Math.Max(1024, reserve);
            data = (byte*)Unity.Collections.LowLevel.Unsafe.UnsafeUtility.Malloc(capacity, 1, Unity.Collections.Allocator.Persistent);
            length = 0;
            failed = false;
        }

        public void Write(byte* source, int count)
        {
            if (failed || data == null)
                return;
            if ((long)length + count > capacity)
            {
                long grown = Math.Max((long)capacity * 2, (long)length + count);
                if (grown > int.MaxValue)
                {
                    failed = true;
                    return;
                }
                var bigger = (byte*)Unity.Collections.LowLevel.Unsafe.UnsafeUtility.Malloc(grown, 1, Unity.Collections.Allocator.Persistent);
                Unity.Collections.LowLevel.Unsafe.UnsafeUtility.MemCpy(bigger, data, length);
                Unity.Collections.LowLevel.Unsafe.UnsafeUtility.Free(data, Unity.Collections.Allocator.Persistent);
                data = bigger;
                capacity = (int)grown;
            }
            Unity.Collections.LowLevel.Unsafe.UnsafeUtility.MemCpy(data + length, source, count);
            length += count;
        }

        public void Free()
        {
            if (data != null)
                Unity.Collections.LowLevel.Unsafe.UnsafeUtility.Free(data, Unity.Collections.Allocator.Persistent);
            data = null;
            length = capacity = 0;
        }
    }

    static class NativeDeflate
    {
        delegate int Feeder(IntPtr buffer, int length, IntPtr state);

        [DllImport("MonoPosixHelper")]
        static extern IntPtr CreateZStream(int compress, byte gzip, IntPtr feeder, IntPtr state);

        [DllImport("MonoPosixHelper")]
        static extern int CloseZStream(IntPtr stream);

        [DllImport("MonoPosixHelper")]
        static extern int WriteZStream(IntPtr stream, IntPtr buffer, int length);

        static readonly Feeder FeederDelegate = Feed;
        static IntPtr _feeder;
        static volatile int _state;

        public static bool available => _state >= 0;

        [AOT.MonoPInvokeCallback(typeof(Feeder))]
        static unsafe int Feed(IntPtr buffer, int length, IntPtr state)
        {
            try
            {
                var target = GCHandle.FromIntPtr(state).Target;
                if (target is NativeSink sink)
                    sink.Write((byte*)buffer, length);
                else
                    ((RecordingBuffer)target).WriteBytes((byte*)buffer, length);
                return length;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        public static unsafe bool TryCompress(ReadOnlySpan<byte> payload, IntPtr output,
            List<float[,]> heights = null, ReadOnlySpan<byte> holes = default)
        {
            if (_state < 0)
                return false;
            try
            {
                if (_feeder == IntPtr.Zero)
                    _feeder = Marshal.GetFunctionPointerForDelegate(FeederDelegate);
                var stream = CreateZStream(1, 0, _feeder, output);
                if (stream == IntPtr.Zero)
                {
                    _state = -1;
                    return false;
                }
                bool wrote = WriteInput(stream, payload);
                if (wrote && heights != null)
                {
                    foreach (var rows in heights)
                    {
                        fixed (float* samples = &rows[0, 0])
                            wrote = WriteInput(stream, new ReadOnlySpan<byte>(samples, rows.Length * sizeof(float)));
                        if (!wrote)
                            break;
                    }
                }
                wrote = wrote && WriteInput(stream, holes);
                int closed = CloseZStream(stream);
                if (!wrote || closed < 0)
                    return false;
                _state = 1;
                return true;
            }
            catch (Exception)
            {
                _state = -1;
                return false;
            }
        }

        static unsafe bool WriteInput(IntPtr stream, ReadOnlySpan<byte> input)
        {
            if (input.IsEmpty)
                return true;
            fixed (byte* bytes = input)
                return WriteZStream(stream, (IntPtr)bytes, input.Length) >= 0;
        }
    }
#endif
}
#endif
