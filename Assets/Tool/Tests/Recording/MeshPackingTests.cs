using System;
using NUnit.Framework;
using PurrNet.Recording;

/// <summary>MESH_PACKED keeps every index and every vertex to within 1/131070 of the mesh's size on each axis.</summary>
public class MeshPackingTests
{
    static byte[] MeshPayload(float[] vertices, int[] indices, out int length)
    {
        var buffer = new RecordingBuffer();
        buffer.WriteVarUInt(9);
        buffer.WriteString("Crate");
        buffer.WriteVarUInt((ulong)(vertices.Length / 3));
        foreach (float v in vertices)
            buffer.WriteFloat(v);
        buffer.WriteVarUInt((ulong)indices.Length);
        foreach (int i in indices)
        {
            buffer.WriteByte((byte)i);
            buffer.WriteByte((byte)(i >> 8));
            buffer.WriteByte((byte)(i >> 16));
            buffer.WriteByte((byte)(i >> 24));
        }
        length = buffer.length;
        return buffer.data;
    }

    static uint VarUInt(byte[] data, ref int at)
    {
        uint value = 0;
        for (int shift = 0; ; shift += 7)
        {
            byte b = data[at++];
            value |= (uint)(b & 0x7F) << shift;
            if (b < 0x80)
                return value;
        }
    }

    /// <summary>What the viewer does with a MESH_PACKED payload.</summary>
    static (string name, float[] vertices, int[] indices) Unpack(byte[] data, int length)
    {
        int at = 0;
        Assert.That(VarUInt(data, ref at), Is.EqualTo(9));
        int nameLength = (int)VarUInt(data, ref at);
        string name = System.Text.Encoding.UTF8.GetString(data, at, nameLength);
        at += nameLength;
        int n = (int)VarUInt(data, ref at);
        var min = new float[3];
        var extent = new float[3];
        for (int a = 0; a < 3; a++)
            min[a] = BitConverter.ToSingle(data, at + 4 * a);
        for (int a = 0; a < 3; a++)
            extent[a] = BitConverter.ToSingle(data, at + 12 + 4 * a);
        at += 24;
        var vertices = new float[n * 3];
        for (int a = 0; a < 3; a++)
        {
            int q = 0;
            for (int v = 0; v < n; v++)
            {
                int z = data[at + v] | data[at + n + v] << 8;
                q = (q + ((z >> 1) ^ -(z & 1))) & 0xFFFF;
                vertices[v * 3 + a] = min[a] + q / 65535f * extent[a];
            }
            at += 2 * n;
        }
        int m = (int)VarUInt(data, ref at);
        var indices = new int[m];
        int last = 0;
        for (int i = 0; i < m; i++)
        {
            uint z = (uint)(data[at + i] | data[at + m + i] << 8 | data[at + 2 * m + i] << 16 | data[at + 3 * m + i] << 24);
            last += (int)(z >> 1) ^ -(int)(z & 1);
            indices[i] = last;
        }
        Assert.That(at + 4 * m, Is.EqualTo(length), "nothing left over");
        return (name, vertices, indices);
    }

    [Test]
    public void VerticesKeepToTheirBoundsAndIndicesAreExact()
    {
        var random = new Random(5);
        var vertices = new float[300 * 3];
        for (int i = 0; i < vertices.Length; i++)
            vertices[i] = (float)(random.NextDouble() * (i % 3 == 1 ? 0.5 : 2000.0) - 1000.0);
        vertices[3] = 1000f;
        var indices = new int[600];
        for (int i = 0; i < indices.Length; i++)
            indices[i] = random.Next(0, 300);
        indices[7] = 299;
        indices[8] = 0;
        var payload = MeshPayload(vertices, indices, out int length);

        var packed = new byte[MeshPacking.MaxBytes(length)];
        int packedLength = MeshPacking.Pack(payload, length, packed);
        Assert.That(packedLength, Is.GreaterThan(0).And.LessThan(length));
        var (name, back, backIndices) = Unpack(packed, packedLength);
        Assert.That(name, Is.EqualTo("Crate"));
        Assert.That(backIndices, Is.EqualTo(indices));
        for (int i = 0; i < vertices.Length; i++)
        {
            float size = i % 3 == 1 ? 0.5f : 2000f;
            Assert.That(back[i], Is.EqualTo(vertices[i]).Within(size / 131070f * 1.01f + 1e-4f), $"coordinate {i}");
        }
    }

    [Test]
    public void AFlatOrEmptyMeshPacksAndANonFiniteOneDoesNot()
    {
        var flat = MeshPayload(new[] { 1f, 0f, 3f, 2f, 0f, 3f, 1f, 0f, 4f }, new[] { 0, 1, 2 }, out int length);
        var packed = new byte[MeshPacking.MaxBytes(length)];
        int packedLength = MeshPacking.Pack(flat, length, packed);
        var (_, back, _) = Unpack(packed, packedLength);
        Assert.That(back[1], Is.EqualTo(0f), "an axis with no extent stays where it is");
        Assert.That(back[3], Is.EqualTo(2f));

        var empty = MeshPayload(Array.Empty<float>(), Array.Empty<int>(), out length);
        packed = new byte[MeshPacking.MaxBytes(length)];
        Assert.That(MeshPacking.Pack(empty, length, packed), Is.GreaterThan(0));

        var broken = MeshPayload(new[] { float.NaN, 0f, 0f }, new[] { 0, 0, 0 }, out length);
        packed = new byte[MeshPacking.MaxBytes(length)];
        Assert.That(MeshPacking.Pack(broken, length, packed), Is.EqualTo(-1), "written as a plain MESH instead");
    }
}
