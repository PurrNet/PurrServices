using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using PurrNet.Recording;

/// <summary>
/// A per-tick stream whose rows in a chunk only repeat the row it held at the end of the chunk before is not written
/// again: the chunk lists it among its HOLDS instead (see ChunkEncoder).
/// </summary>
public class ChunkEncoderHoldTests
{
    static RowLayout Layout()
    {
        var layout = new RowLayout { id = 1, rowSize = 4 };
        layout.columns.Add(new RowLayout.Column { name = "value", type = "i32", offset = 0, size = 4 });
        return layout;
    }

    static readonly RowLayout Int = Layout();

    /// <summary>A chunk's log as the session writes it: per row the slot (and the tick when it changes), then the row.</summary>
    static ChunkLog Chunk(int file, ulong first, ulong count, params (int id, ulong tick, int value)[] rows)
    {
        var log = new ChunkLog { firstTick = first, tickCount = count, file = file };
        var slots = new List<int>();
        ulong? lastTick = null;
        foreach (var row in rows.OrderBy(r => r.tick))
        {
            int slot = slots.IndexOf(row.id);
            if (slot < 0)
            {
                slot = slots.Count;
                slots.Add(row.id);
            }
            if (lastTick == row.tick)
                log.bytes.WriteInt32(slot);
            else
            {
                log.bytes.WriteInt32(slot | ChunkLog.TickFollows);
                log.bytes.WriteUInt64(row.tick);
                lastTick = row.tick;
            }
            log.bytes.WriteInt32(row.value);
        }
        foreach (int id in slots)
            log.streams.Add(new ChunkStream { id = id, layout = Int, forwardFill = true, baseSlot = -1, lastTick = first + count - 1 });
        return log;
    }

    sealed class Decoded
    {
        public readonly List<int> blocks = new List<int>();
        public readonly List<(int id, ulong end)> holds = new List<(int, ulong)>();
    }

    static Decoded Read(RecordingBuffer buffer)
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
        var result = new Decoded();
        ulong first = V();
        V();
        int blocks = (int)V();
        for (int i = 0; i < blocks; i++)
        {
            int id = (int)V();
            int rows = (int)V();
            for (int r = 0; r < rows; r++)
                V();
            V();
            at += rows * 4 + 4;
            result.blocks.Add(id);
        }
        Assert.That(at, Is.LessThan(buffer.length), "the chunk ends with its holds");
        int holds = (int)V();
        int previous = 0;
        for (int i = 0; i < holds; i++)
        {
            previous += (int)V();
            result.holds.Add((previous, first + V()));
        }
        Assert.That(at, Is.EqualTo(buffer.length));
        return result;
    }

    [Test]
    public void AStreamThatOnlyRepeatsItsRowIsHeldNotWritten()
    {
        var encoder = new ChunkEncoder();
        var a = Read(encoder.Encode(Chunk(1, 0, 10, (1, 0, 5), (2, 0, 7))));
        Assert.That(a.blocks, Is.EqualTo(new[] { 1, 2 }));
        Assert.That(a.holds, Is.Empty, "nothing to hold in a file's first chunk");

        var b = Read(encoder.Encode(Chunk(1, 10, 10, (1, 10, 5), (2, 10, 7), (2, 15, 8))));
        Assert.That(b.blocks, Is.EqualTo(new[] { 2 }), "the stream that changed is written");
        Assert.That(b.holds, Is.EqualTo(new[] { (1, 19UL) }), "the one that did not is held to the chunk's end");

        var c = Read(encoder.Encode(Chunk(1, 20, 10, (1, 20, 5), (2, 20, 8))));
        Assert.That(c.blocks, Is.Empty);
        Assert.That(c.holds, Is.EqualTo(new[] { (1, 29UL), (2, 29UL) }), "held again, and a row held since it changed");

        var d = Read(encoder.Encode(Chunk(1, 30, 10, (1, 30, 6))));
        Assert.That(d.blocks, Is.EqualTo(new[] { 1 }), "a new value is written");
        Assert.That(d.holds, Is.Empty, "a stream not in the chunk at all is not held");

        var e = Read(encoder.Encode(Chunk(1, 40, 10, (2, 40, 8))));
        Assert.That(e.blocks, Is.EqualTo(new[] { 2 }), "a stream that was missing from a chunk is written again, not held across the gap");
    }

    [Test]
    public void ANewFileStartsWithoutHolds()
    {
        var encoder = new ChunkEncoder();
        Read(encoder.Encode(Chunk(1, 0, 10, (1, 0, 5))));
        var next = Read(encoder.Encode(Chunk(2, 10, 10, (1, 10, 5))));
        Assert.That(next.blocks, Is.EqualTo(new[] { 1 }));
        Assert.That(next.holds, Is.Empty);
    }

    [Test]
    public void AStreamThatEndsInsideTheChunkIsHeldToItsLastTick()
    {
        var encoder = new ChunkEncoder();
        Read(encoder.Encode(Chunk(1, 0, 10, (1, 0, 5))));
        var log = Chunk(1, 10, 10, (1, 10, 5));
        var stream = log.streams[0];
        stream.lastTick = 13;
        log.streams[0] = stream;
        Assert.That(Read(encoder.Encode(log)).holds, Is.EqualTo(new[] { (1, 13UL) }));
    }
}
