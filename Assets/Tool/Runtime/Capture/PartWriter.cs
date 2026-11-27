using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace PurrNet.Services.Capture
{
    /// <summary>How a column's numbers are written: the difference from the row before, or the bits that changed.</summary>
    public enum PartColumnKind : byte
    {
        Delta = 0,
        Bits = 1
    }

    /// <summary>
    /// One column of a stream. Every value is a whole number in the file; <see cref="scale"/> says what a
    /// fractional value was multiplied by before it was rounded (1000: thousandths), 0 for numbers that were whole
    /// to begin with.
    /// </summary>
    public readonly struct PartColumn
    {
        public readonly string name;
        public readonly PartColumnKind kind;
        public readonly double scale;

        public PartColumn(string name, double scale = 0, PartColumnKind kind = PartColumnKind.Delta)
        {
            this.name = name;
            this.scale = scale;
            this.kind = kind;
        }

        public static PartColumn Bits(string name) => new PartColumn(name, 0, PartColumnKind.Bits);
    }

    /// <summary>
    /// Writes a <c>.purrpart</c> file: what one player's device did during a match (camera, input), kept apart from
    /// the game's own traffic. No Unity types: the same code runs in tests outside the engine.
    /// <para>
    /// The file is the eight bytes <c>PURRPART</c>, a version byte, then records: a kind byte (bit 7 set when the
    /// payload is deflated), the payload's length as a varint, the payload. A header (JSON) comes first, a stream
    /// record (JSON) before a stream's first rows, notes (JSON) wherever something is worth saying once, and a
    /// footer (JSON) when the file was closed properly. A file that ends without one is complete up to its last
    /// record.
    /// </para>
    /// <para>
    /// Rows wait in memory until <see cref="EndChunk"/> writes them as one chunk record: the number of blocks, then
    /// per stream its id, its row count and its columns one after the other, the tick first. A value is written as
    /// the zigzag difference from the row above it (<see cref="PartColumnKind.Delta"/>) or XORed with it
    /// (<see cref="PartColumnKind.Bits"/>); the first row of a chunk counts from zero, so every chunk reads on its
    /// own. A stream holds its last row until the next one: <see cref="WriteChanged"/> leaves out rows that say
    /// nothing new.
    /// </para>
    /// </summary>
    public sealed class PartWriter : IDisposable
    {
        public const byte KindHeader = 1, KindStream = 2, KindChunk = 3, KindNote = 4, KindFooter = 5;
        public const byte Deflated = 0x80;
        public const byte Version = 1;
        public const string Extension = ".purrpart";

        static readonly byte[] Magic = Encoding.ASCII.GetBytes("PURRPART");

        sealed class StreamState
        {
            public int id;
            public string name;
            public PartColumn[] columns;
            public bool declared;
            public long[] rows = new long[256];
            public int count;
            public long[] last;
            public bool hasLast;
        }

        readonly Stream _output;
        readonly List<StreamState> _streams = new List<StreamState>();
        readonly MemoryStream _packed = new MemoryStream();
        readonly byte[] _lengthBytes = new byte[10];
        byte[] _payload = new byte[4096];
        int _payloadLength;
        bool _closed;

        /// <summary>Bytes handed to the output so far.</summary>
        public long bytesWritten { get; private set; }

        /// <summary>Rows written since the file was opened.</summary>
        public long rowsWritten { get; private set; }

        /// <summary>Rows waiting for the next <see cref="EndChunk"/>.</summary>
        public int pendingRows { get; private set; }

        public PartWriter(Stream output, string headerJson)
        {
            _output = output ?? throw new ArgumentNullException(nameof(output));
            _output.Write(Magic, 0, Magic.Length);
            _output.WriteByte(Version);
            bytesWritten = Magic.Length + 1;
            WriteJson(KindHeader, headerJson);
        }

        /// <summary>Declares a stream and returns the id its rows are written with. Every row starts with its tick.</summary>
        public int AddStream(string name, params PartColumn[] columns)
        {
            var state = new StreamState
            {
                id = _streams.Count,
                name = name,
                columns = columns,
                last = new long[columns.Length]
            };
            _streams.Add(state);
            return state.id;
        }

        /// <summary>Adds a row. <paramref name="values"/> has one number per column of the stream.</summary>
        public void WriteRow(int stream, long tick, long[] values)
        {
            if (_closed)
                return;
            var state = _streams[stream];
            int width = state.columns.Length + 1;
            int at = state.count * width;
            if (at + width > state.rows.Length)
                Array.Resize(ref state.rows, state.rows.Length * 2);
            state.rows[at] = tick;
            Array.Copy(values, 0, state.rows, at + 1, width - 1);
            Array.Copy(values, state.last, width - 1);
            state.hasLast = true;
            state.count++;
            pendingRows++;
            rowsWritten++;
        }

        /// <summary>Adds a row unless it equals the stream's last one. True when it was written.</summary>
        public bool WriteChanged(int stream, long tick, long[] values)
        {
            if (_closed)
                return false;
            var state = _streams[stream];
            if (state.hasLast)
            {
                bool same = true;
                var last = state.last;
                for (int i = 0; i < last.Length; i++)
                {
                    if (last[i] != values[i])
                    {
                        same = false;
                        break;
                    }
                }
                if (same)
                    return false;
            }
            WriteRow(stream, tick, values);
            return true;
        }

        /// <summary>Writes a note: something said once, as JSON.</summary>
        public void WriteNote(string json)
        {
            if (!_closed)
                WriteJson(KindNote, json);
        }

        /// <summary>Writes the rows gathered since the last chunk and hands them to the output.</summary>
        public void EndChunk()
        {
            if (_closed || pendingRows == 0)
                return;

            int blocks = 0;
            for (int s = 0; s < _streams.Count; s++)
            {
                var state = _streams[s];
                if (state.count == 0)
                    continue;
                blocks++;
                if (!state.declared)
                {
                    state.declared = true;
                    WriteJson(KindStream, DescribeStream(state));
                }
            }

            _payloadLength = 0;
            PutVarUInt((ulong)blocks);
            for (int s = 0; s < _streams.Count; s++)
            {
                var state = _streams[s];
                int count = state.count;
                if (count == 0)
                    continue;
                int width = state.columns.Length + 1;
                var rows = state.rows;
                PutVarUInt((ulong)state.id);
                PutVarUInt((ulong)count);

                long previous = 0;
                for (int r = 0; r < count; r++)
                {
                    long value = rows[r * width];
                    PutVarUInt(ZigZag(value - previous));
                    previous = value;
                }

                for (int c = 1; c < width; c++)
                {
                    bool bits = state.columns[c - 1].kind == PartColumnKind.Bits;
                    previous = 0;
                    for (int r = 0; r < count; r++)
                    {
                        long value = rows[r * width + c];
                        PutVarUInt(bits ? (ulong)(value ^ previous) : ZigZag(value - previous));
                        previous = value;
                    }
                }
                state.count = 0;
            }
            pendingRows = 0;

            _packed.SetLength(0);
            using (var deflate = new DeflateStream(_packed, System.IO.Compression.CompressionLevel.Optimal, true))
                deflate.Write(_payload, 0, _payloadLength);
            WriteRecord((byte)(KindChunk | Deflated), _packed.GetBuffer(), (int)_packed.Length);
            _output.Flush();
        }

        /// <summary>Writes what is waiting and the footer. Nothing can be written afterwards.</summary>
        public void Close(string footerJson)
        {
            if (_closed)
                return;
            EndChunk();
            WriteJson(KindFooter, footerJson);
            _output.Flush();
            _closed = true;
        }

        public void Dispose()
        {
            _closed = true;
            _packed.Dispose();
        }

        static string DescribeStream(StreamState state)
        {
            var json = new PartJson().BeginObject().Prop("id", state.id).Prop("name", state.name).Key("columns").BeginArray();
            for (int i = 0; i < state.columns.Length; i++)
            {
                var column = state.columns[i];
                json.BeginObject().Prop("name", column.name);
                if (column.kind == PartColumnKind.Bits)
                    json.Prop("kind", "bits");
                if (column.scale != 0)
                    json.Prop("scale", column.scale);
                json.EndObject();
            }
            return json.EndArray().EndObject().ToString();
        }

        void WriteJson(byte kind, string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json ?? "{}");
            WriteRecord(kind, bytes, bytes.Length);
        }

        void WriteRecord(byte kind, byte[] payload, int length)
        {
            _output.WriteByte(kind);
            int n = 0;
            ulong value = (ulong)length;
            while (value >= 0x80)
            {
                _lengthBytes[n++] = (byte)(value | 0x80);
                value >>= 7;
            }
            _lengthBytes[n++] = (byte)value;
            _output.Write(_lengthBytes, 0, n);
            _output.Write(payload, 0, length);
            bytesWritten += 1 + n + length;
        }

        static ulong ZigZag(long value) => (ulong)((value << 1) ^ (value >> 63));

        void PutVarUInt(ulong value)
        {
            if (_payloadLength + 10 > _payload.Length)
                Array.Resize(ref _payload, _payload.Length * 2);
            while (value >= 0x80)
            {
                _payload[_payloadLength++] = (byte)(value | 0x80);
                value >>= 7;
            }
            _payload[_payloadLength++] = (byte)value;
        }
    }
}
