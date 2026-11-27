using System;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using PurrNet.Recording;

/// <summary>
/// Rows of a stream with a [RecordKey] field are stored against the previous row with the same key: read back the
/// way the viewer reads them, they are the rows that were written.
/// </summary>
public class KeyedRowsTests
{
    struct Row
    {
        public int tick;
        public int id;
        public float x;
        public string name;
    }

    static RowLayout Layout()
    {
        var layout = new RowLayout { id = 1, rowSize = 12, keyOffset = 4, keyedFrom = 4, keyName = "id" };
        layout.columns.Add(new RowLayout.Column { name = "tick", type = "i32", offset = 0, size = 4 });
        layout.columns.Add(new RowLayout.Column { name = "id", type = "i32", offset = 4, size = 4 });
        layout.columns.Add(new RowLayout.Column { name = "x", type = "f32", offset = 8, size = 4 });
        layout.managed.Add(new RowLayout.ManagedField { name = "name" });
        return layout;
    }

    static ChunkLog Chunk(RowLayout layout, List<Row> rows)
    {
        var log = new ChunkLog { firstTick = 0, tickCount = 100, file = 1 };
        ulong? lastTick = null;
        foreach (var row in rows)
        {
            if (lastTick == (ulong)row.tick)
                log.bytes.WriteInt32(0);
            else
            {
                log.bytes.WriteInt32(0 | ChunkLog.TickFollows);
                log.bytes.WriteUInt64((ulong)row.tick);
                lastTick = (ulong)row.tick;
            }
            log.bytes.WriteInt32(row.tick);
            log.bytes.WriteInt32(row.id);
            log.bytes.WriteFloat(row.x);
            var name = Encoding.UTF8.GetBytes(row.name);
            log.bytes.WriteInt32(name.Length);
            log.bytes.WriteBytes(name, 0, name.Length);
        }
        log.streams.Add(new ChunkStream { id = 3, layout = layout, forwardFill = false, baseSlot = -1, lastTick = 99 });
        return log;
    }

    /// <summary>What the viewer does with a keyed block.</summary>
    static List<Row> Read(RecordingBuffer buffer, out int managedZeros)
    {
        int at = 0;
        ulong V()
        {
            ulong value = 0;
            for (int shift = 0; ; shift += 7)
            {
                byte b = buffer.data[at++];
                value |= (ulong)(b & 0x7F) << shift;
                if (b < 0x80)
                    return value;
            }
        }
        V();
        V();
        Assert.That(V(), Is.EqualTo(1));
        Assert.That(V(), Is.EqualTo(3));
        int n = (int)V();
        for (int i = 0; i < n; i++)
            V();
        var planes = new byte[n * 12];
        Array.Copy(buffer.data, at, planes, 0, planes.Length);
        at += planes.Length;
        var rows = new byte[n * 12];
        // tick: second differences, zigzag
        uint prev = 0, delta = 0;
        for (int r = 0; r < n; r++)
        {
            uint z = (uint)(planes[r] | planes[n + r] << 8 | planes[2 * n + r] << 16 | planes[3 * n + r] << 24);
            delta += (z >> 1) ^ (uint)-(int)(z & 1);
            prev += delta;
            BitConverter.GetBytes(prev).CopyTo(rows, r * 12);
        }
        for (int b = 4; b < 12; b++)
            for (int r = 0; r < n; r++)
                rows[r * 12 + b] = planes[b * n + r];
        var last = new Dictionary<int, int>();
        var previousOf = new int[n];
        for (int r = 0; r < n; r++)
        {
            int id = BitConverter.ToInt32(rows, r * 12 + 4);
            previousOf[r] = last.TryGetValue(id, out int before) ? before : -1;
            if (previousOf[r] >= 0)
                for (int b = 8; b < 12; b++)
                    rows[r * 12 + b] ^= rows[previousOf[r] * 12 + b];
            last[id] = r;
        }
        var result = new List<Row>();
        managedZeros = 0;
        var names = new string[n];
        for (int r = 0; r < n; r++)
        {
            int length = (int)V();
            if (length == 0)
            {
                managedZeros++;
                Assert.That(previousOf[r], Is.GreaterThanOrEqualTo(0), "a 0 refers to the key's previous row");
                names[r] = names[previousOf[r]];
            }
            else
            {
                names[r] = Encoding.UTF8.GetString(buffer.data, at, length - 1);
                at += length - 1;
            }
            result.Add(new Row
            {
                tick = BitConverter.ToInt32(rows, r * 12), id = BitConverter.ToInt32(rows, r * 12 + 4),
                x = BitConverter.ToSingle(rows, r * 12 + 8), name = names[r]
            });
        }
        Assert.That(at, Is.EqualTo(buffer.length - 1), "then the (empty) holds list");
        return result;
    }

    [Test]
    public void InterleavedElementsReadBackAsWritten()
    {
        var rows = new List<Row>();
        var random = new Random(11);
        for (int tick = 0; tick < 40; tick++)
            for (int id = 1; id <= 5; id++)
                if (random.Next(3) > 0)
                    rows.Add(new Row { tick = tick, id = id * 1000, x = id * 10 + tick * (id % 2) * 0.5f, name = "element " + id + (tick > 30 && id == 2 ? " renamed" : "") });
        var encoded = new ChunkEncoder().Encode(Chunk(Layout(), rows));
        var back = Read(encoded, out int zeros);
        Assert.That(back.Count, Is.EqualTo(rows.Count));
        for (int i = 0; i < rows.Count; i++)
        {
            Assert.That(back[i].tick, Is.EqualTo(rows[i].tick), $"row {i}");
            Assert.That(back[i].id, Is.EqualTo(rows[i].id), $"row {i}");
            Assert.That(back[i].x, Is.EqualTo(rows[i].x), $"row {i}");
            Assert.That(back[i].name, Is.EqualTo(rows[i].name), $"row {i}");
        }
        Assert.That(zeros, Is.GreaterThan(rows.Count - 10), "names are written once per element, not every time elements alternate");
    }
}
