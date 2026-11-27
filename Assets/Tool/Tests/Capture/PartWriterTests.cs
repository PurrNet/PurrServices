using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using NUnit.Framework;
using PurrNet.Services.Capture;

public class PartWriterTests
{
    sealed class Block
    {
        public int stream;
        public List<long> ticks = new List<long>();
        public List<long[]> rows = new List<long[]>();
    }

    sealed class Read
    {
        public string header, footer;
        public List<string> streams = new List<string>();
        public List<string> notes = new List<string>();
        public List<List<Block>> chunks = new List<List<Block>>();
    }

    static ulong VarUInt(byte[] data, ref int at)
    {
        ulong value = 0;
        int shift = 0;
        while (true)
        {
            byte b = data[at++];
            value |= (ulong)(b & 0x7f) << shift;
            if ((b & 0x80) == 0)
                return value;
            shift += 7;
        }
    }

    static long UnZigZag(ulong value) => (long)(value >> 1) ^ -(long)(value & 1);

    /// <summary>Reads a part file the way its format is described. Stops at a record that is cut short.</summary>
    static Read ReadFile(byte[] file, Dictionary<int, PartColumn[]> columns)
    {
        Assert.That(Encoding.ASCII.GetString(file, 0, 8), Is.EqualTo("PURRPART"));
        Assert.That(file[8], Is.EqualTo(PartWriter.Version));
        var read = new Read();
        int at = 9;
        while (at < file.Length)
        {
            byte kind = file[at++];
            int length;
            try
            {
                length = (int)VarUInt(file, ref at);
            }
            catch (System.IndexOutOfRangeException)
            {
                break;
            }
            if (at + length > file.Length)
                break;
            var payload = new byte[length];
            System.Array.Copy(file, at, payload, 0, length);
            at += length;

            if ((kind & PartWriter.Deflated) != 0)
            {
                using var inflated = new MemoryStream();
                using (var deflate = new DeflateStream(new MemoryStream(payload), CompressionMode.Decompress))
                    deflate.CopyTo(inflated);
                payload = inflated.ToArray();
            }

            switch (kind & 0x7f)
            {
                case PartWriter.KindHeader: read.header = Encoding.UTF8.GetString(payload); break;
                case PartWriter.KindStream: read.streams.Add(Encoding.UTF8.GetString(payload)); break;
                case PartWriter.KindNote: read.notes.Add(Encoding.UTF8.GetString(payload)); break;
                case PartWriter.KindFooter: read.footer = Encoding.UTF8.GetString(payload); break;
                case PartWriter.KindChunk:
                {
                    var blocks = new List<Block>();
                    int p = 0;
                    int count = (int)VarUInt(payload, ref p);
                    for (int b = 0; b < count; b++)
                    {
                        var block = new Block { stream = (int)VarUInt(payload, ref p) };
                        int rows = (int)VarUInt(payload, ref p);
                        var layout = columns[block.stream];
                        long previous = 0;
                        for (int r = 0; r < rows; r++)
                        {
                            previous += UnZigZag(VarUInt(payload, ref p));
                            block.ticks.Add(previous);
                            block.rows.Add(new long[layout.Length]);
                        }
                        for (int c = 0; c < layout.Length; c++)
                        {
                            previous = 0;
                            for (int r = 0; r < rows; r++)
                            {
                                ulong raw = VarUInt(payload, ref p);
                                previous = layout[c].kind == PartColumnKind.Bits ? previous ^ (long)raw : previous + UnZigZag(raw);
                                block.rows[r][c] = previous;
                            }
                        }
                        blocks.Add(block);
                    }
                    Assert.That(p, Is.EqualTo(payload.Length));
                    read.chunks.Add(blocks);
                    break;
                }
            }
        }
        return read;
    }

    static readonly PartColumn[] Pose = { new PartColumn("x", 1000), new PartColumn("y", 1000), PartColumn.Bits("keys") };
    static readonly PartColumn[] Count = { new PartColumn("n") };

    [Test]
    public void RowsComeBackAsWritten()
    {
        var stream = new MemoryStream();
        var writer = new PartWriter(stream, "{\"format\":\"purrpart\"}");
        int pose = writer.AddStream("pose", Pose);
        int count = writer.AddStream("count", Count);

        writer.WriteRow(pose, 100, new long[] { 1500, -2500, 0x10 });
        writer.WriteRow(pose, 103, new long[] { -40, long.MaxValue / 2, unchecked((long)0x8000000000000001) });
        writer.WriteRow(count, 101, new long[] { 7 });
        writer.WriteNote("{\"note\":1}");
        writer.EndChunk();
        writer.WriteRow(pose, 90, new long[] { 0, 0, 0 });
        writer.Close("{\"clean\":true}");

        var layout = new Dictionary<int, PartColumn[]> { [pose] = Pose, [count] = Count };
        var read = ReadFile(stream.ToArray(), layout);

        Assert.That(read.header, Is.EqualTo("{\"format\":\"purrpart\"}"));
        Assert.That(read.footer, Is.EqualTo("{\"clean\":true}"));
        Assert.That(read.notes, Is.EqualTo(new[] { "{\"note\":1}" }));
        Assert.That(read.streams, Has.Count.EqualTo(2));
        Assert.That(read.streams[0], Does.Contain("\"name\":\"pose\"").And.Contain("\"scale\":1000").And.Contain("\"kind\":\"bits\""));
        Assert.That(read.chunks, Has.Count.EqualTo(2));

        var first = read.chunks[0];
        Assert.That(first, Has.Count.EqualTo(2));
        Assert.That(first[0].ticks, Is.EqualTo(new long[] { 100, 103 }));
        Assert.That(first[0].rows[0], Is.EqualTo(new long[] { 1500, -2500, 0x10 }));
        Assert.That(first[0].rows[1], Is.EqualTo(new long[] { -40, long.MaxValue / 2, unchecked((long)0x8000000000000001) }));
        Assert.That(first[1].ticks, Is.EqualTo(new long[] { 101 }));
        Assert.That(first[1].rows[0], Is.EqualTo(new long[] { 7 }));

        // A tick may go back (a client that reconnects starts counting again), and a chunk reads on its own.
        Assert.That(read.chunks[1][0].ticks, Is.EqualTo(new long[] { 90 }));
        Assert.That(read.chunks[1][0].rows[0], Is.EqualTo(new long[] { 0, 0, 0 }));
        Assert.That(writer.bytesWritten, Is.EqualTo(stream.Length));
    }

    [Test]
    public void UnchangedRowsAreLeftOut()
    {
        var stream = new MemoryStream();
        var writer = new PartWriter(stream, "{}");
        int pose = writer.AddStream("pose", Pose);

        Assert.That(writer.WriteChanged(pose, 1, new long[] { 1, 2, 3 }), Is.True);
        Assert.That(writer.WriteChanged(pose, 2, new long[] { 1, 2, 3 }), Is.False);
        writer.EndChunk();
        // Still the same after a chunk ended: a stream holds its last row across chunks.
        Assert.That(writer.WriteChanged(pose, 3, new long[] { 1, 2, 3 }), Is.False);
        Assert.That(writer.WriteChanged(pose, 4, new long[] { 1, 2, 4 }), Is.True);
        writer.Close("{}");

        var read = ReadFile(stream.ToArray(), new Dictionary<int, PartColumn[]> { [pose] = Pose });
        Assert.That(writer.rowsWritten, Is.EqualTo(2));
        Assert.That(read.chunks[0][0].ticks, Is.EqualTo(new long[] { 1 }));
        Assert.That(read.chunks[1][0].ticks, Is.EqualTo(new long[] { 4 }));
    }

    [Test]
    public void AFileCutShortReadsUpToItsLastRecord()
    {
        var stream = new MemoryStream();
        var writer = new PartWriter(stream, "{}");
        int count = writer.AddStream("count", Count);
        writer.WriteRow(count, 1, new long[] { 1 });
        writer.EndChunk();
        long whole = stream.Length;
        writer.WriteRow(count, 2, new long[] { 2 });
        writer.EndChunk();

        var cut = new byte[whole + 3];
        System.Array.Copy(stream.ToArray(), cut, cut.Length);
        var read = ReadFile(cut, new Dictionary<int, PartColumn[]> { [count] = Count });
        Assert.That(read.chunks, Has.Count.EqualTo(1));
        Assert.That(read.footer, Is.Null);
    }

    [Test]
    public void NothingIsWrittenAfterClose()
    {
        var stream = new MemoryStream();
        var writer = new PartWriter(stream, "{}");
        int count = writer.AddStream("count", Count);
        writer.Close("{}");
        long length = stream.Length;
        writer.WriteRow(count, 1, new long[] { 1 });
        writer.WriteNote("{}");
        writer.EndChunk();
        Assert.That(stream.Length, Is.EqualTo(length));
    }

    [Test]
    public void JsonEscapesWhatItMust()
    {
        string json = new PartJson().BeginObject()
            .Prop("text", "a\"b\\c\n\u0001").Prop("n", 3).Prop("f", 0.5).Prop("yes", true).Prop("none", (string)null)
            .Key("list").BeginArray().Value(1).Value("x").EndArray()
            .Raw("raw", "{\"k\":1}")
            .EndObject().ToString();
        Assert.That(json, Is.EqualTo(
            "{\"text\":\"a\\\"b\\\\c\\n\\u0001\",\"n\":3,\"f\":0.5,\"yes\":true,\"none\":null,\"list\":[1,\"x\"],\"raw\":{\"k\":1}}"));
    }
}
