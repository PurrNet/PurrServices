using System;
using NUnit.Framework;
using PurrNet.Recording;

/// <summary>
/// Columns on a grid are stored as integers, after the byte planes of the other bytes, as varints of their zigzag
/// second differences: read back the way the viewer reads them, they are the floats that were recorded.
/// </summary>
public class GridColumnsTests
{
    [Test]
    public void GridColumnsReadBackAsTheRecordedFloats()
    {
        var layout = new RowLayout { id = 1, rowSize = 9, hasGrid = true };
        layout.columns.Add(new RowLayout.Column { name = "position.x", type = "f32", offset = 0, size = 4, grid = 1024f });
        layout.columns.Add(new RowLayout.Column { name = "visible", type = "u8", offset = 4, size = 1 });
        layout.columns.Add(new RowLayout.Column { name = "position.y", type = "f32", offset = 5, size = 4, grid = 1024f });

        var values = new (float x, byte visible, float y)[]
        {
            (0f, 1, -3f), (1.5f, 1, -2.9990234375f), (3f, 1, 100000.0009765625f), (4.5f, 0, -100000f), (12345.25f, 1, 0f)
        };
        var log = new ChunkLog { firstTick = 0, tickCount = 10, file = 1 };
        for (int i = 0; i < values.Length; i++)
        {
            log.bytes.WriteInt32(0 | ChunkLog.TickFollows);
            log.bytes.WriteUInt64((ulong)i);
            log.bytes.WriteFloat(values[i].x);
            log.bytes.WriteByte(values[i].visible);
            log.bytes.WriteFloat(values[i].y);
        }
        log.streams.Add(new ChunkStream { id = 4, layout = layout, forwardFill = false, baseSlot = -1, lastTick = 9 });
        var buffer = new ChunkEncoder().Encode(log);

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
        Assert.That(V(), Is.EqualTo(4));
        int n = (int)V();
        Assert.That(n, Is.EqualTo(values.Length));
        for (int i = 0; i < n; i++)
            V();
        // One plane: the byte that is not on a grid (XORed with the row before).
        byte visible = 0;
        for (int r = 0; r < n; r++)
        {
            visible ^= buffer.data[at + r];
            Assert.That(visible, Is.EqualTo(values[r].visible));
        }
        at += n;
        foreach (int column in new[] { 0, 1 })
        {
            int prev = 0, delta = 0;
            for (int r = 0; r < n; r++)
            {
                uint z = (uint)V();
                delta += (int)(z >> 1) ^ -(int)(z & 1);
                prev += delta;
                float expected = column == 0 ? values[r].x : values[r].y;
                Assert.That(prev / 1024f, Is.EqualTo(expected), $"column {column} row {r}");
            }
        }
        Assert.That(V(), Is.EqualTo(0), "no holds");
        Assert.That(at, Is.EqualTo(buffer.length));
    }
}
