using System;
using System.Collections.Generic;
using NUnit.Framework;
using PurrNet.Recording;

/// <summary>TERRAIN_HEIGHTS: the 16-bit samples Unity keeps, packed exactly or within a tolerance (see TerrainHeights).</summary>
public class TerrainHeightsTests
{
    static ulong VarUInt(byte[] data, ref int pos)
    {
        ulong value = 0;
        for (int shift = 0; ; shift += 7)
        {
            byte b = data[pos++];
            value |= (ulong)(b & 0x7F) << shift;
            if (b < 0x80)
                return value;
        }
    }

    /// <summary>
    /// A TERRAIN_HEIGHTS payload turned back into the TERRAIN layout (the heights as floats), the way the viewer
    /// reads it: after the size, the tolerance; each 16-bit sample is its (zigzagged) residual plus the prediction
    /// from the samples left, above and above-left: wrapped to 16 bits when exact, in multiples of
    /// 2 * tolerance + 1 (kept within 0..65535) otherwise.
    /// </summary>
    internal static byte[] UnpackTerrainHeights(byte[] payload)
    {
        int at = 0;
        VarUInt(payload, ref at);
        int nameLength = (int)VarUInt(payload, ref at);
        at += nameLength;
        int side = (int)VarUInt(payload, ref at);
        at += 12;
        int prefix = at;
        int tolerance = (int)VarUInt(payload, ref at);
        int planes = at, samples = side * side, unit = 2 * tolerance + 1;
        int holes = payload.Length - planes - samples * 2;
        var output = new byte[prefix + samples * 4 + holes];
        Array.Copy(payload, output, prefix);
        var heights = new int[samples];
        for (int z = 0; z < side; z++)
        for (int x = 0; x < side; x++)
        {
            int i = z * side + x;
            int zigzag = payload[planes + i] | payload[planes + samples + i] << 8;
            int residual = (short)((zigzag >> 1) ^ -(zigzag & 1));
            int predicted = z == 0 ? (x == 0 ? 0 : heights[i - 1])
                : x == 0 ? heights[i - side] : heights[i - 1] + heights[i - side] - heights[i - side - 1];
            heights[i] = tolerance == 0 ? (predicted + residual) & 0xFFFF : Math.Min(65535, Math.Max(0, predicted + residual * unit));
            BitConverter.GetBytes(heights[i] * (1f / 32766f)).CopyTo(output, prefix + i * 4);
        }
        Array.Copy(payload, planes + samples * 2, output, prefix + samples * 4, holes);
        return output;
    }

    const int Side = 33;

    static List<float[,]> Slices(Func<int, int, int> sample, out int[,] samples)
    {
        var top = new float[10, Side];
        var rest = new float[Side - 10, Side];
        samples = new int[Side, Side];
        for (int z = 0; z < Side; z++)
        for (int x = 0; x < Side; x++)
        {
            int s = sample(z, x);
            samples[z, x] = s;
            float height = s * (1f / 32766f);
            if (z < 10) top[z, x] = height; else rest[z - 10, x] = height;
        }
        return new List<float[,]> { top, rest };
    }

    /// <summary>The record the recorder writes: prefix, packed heights, then a two-byte hole mask.</summary>
    static byte[] Pack(List<float[,]> slices, int tolerance, out int written)
    {
        var prefix = new RecordingBuffer();
        prefix.WriteVarUInt(7);
        prefix.WriteString("Ground");
        prefix.WriteVarUInt(Side);
        prefix.WriteFloat(64f);
        prefix.WriteFloat(10f);
        prefix.WriteFloat(64f);
        var packed = new byte[prefix.length + TerrainHeights.PackedBytes(slices)];
        Array.Copy(prefix.data, packed, prefix.length);
        written = TerrainHeights.TryPack(slices, packed, prefix.length, tolerance);
        if (written < 0)
            return null;
        var record = new byte[prefix.length + written + 2];
        Array.Copy(packed, record, prefix.length + written);
        record[record.Length - 2] = 0xAB;
        record[record.Length - 1] = 0xCD;
        return record;
    }

    [Test]
    public void TerrainHeightsPackExactlyAsSixteenBitSamples()
    {
        var random = new Random(3);
        var slices = Slices((z, x) => random.Next(0, 32767), out var samples);
        var record = Pack(slices, 0, out int written);
        Assert.That(written, Is.EqualTo(1 + 2 * Side * Side), "a one-byte tolerance, then two planes");

        var floats = UnpackTerrainHeights(record);
        int prefix = record.Length - written - 2;
        for (int z = 0; z < Side; z++)
        for (int x = 0; x < Side; x++)
            Assert.That(BitConverter.ToSingle(floats, prefix + (z * Side + x) * 4), Is.EqualTo(samples[z, x] * (1f / 32766f)), $"{x},{z}");
        Assert.That(floats[floats.Length - 2], Is.EqualTo(0xAB), "the hole mask follows the heights");

        slices[0][4, 4] = 0.1234567f;
        Assert.That(Pack(slices, 0, out _), Is.Null, "a height Unity could not have given back keeps the float record");
    }

    [Test]
    public void ATolerancedTerrainIsNeverFurtherThanItsTolerance()
    {
        var random = new Random(9);
        // Smooth, wavy ground with the one-step noise of rounding it to 16 bits.
        var slices = Slices((z, x) => (int)Math.Round(20000 + 900 * Math.Sin(x / 5.0) + 600 * Math.Cos(z / 7.0)) + random.Next(-1, 2),
            out var samples);
        Pack(slices, 0, out int exact);
        foreach (int tolerance in new[] { 1, 2, 4 })
        {
            var record = Pack(slices, tolerance, out int written);
            var floats = UnpackTerrainHeights(record);
            int prefix = record.Length - written - 2;
            for (int z = 0; z < Side; z++)
            for (int x = 0; x < Side; x++)
            {
                int back = (int)Math.Round(BitConverter.ToSingle(floats, prefix + (z * Side + x) * 4) * 32766.0);
                Assert.That(Math.Abs(back - samples[z, x]), Is.LessThanOrEqualTo(tolerance), $"{x},{z} at tolerance {tolerance}");
            }
        }
    }
}
