using System.Buffers.Binary;
using Alexpivo394.Revit.Clash.Geometry;

namespace ToadFlow.Server.Clash.Viewer;

/// <summary>
///     Сетки сцены для веба, формат TCV1 (little-endian): магия, число сеток, затем на сетку индекс элемента в манифесте,
///     число вершин, число индексов, позиции float32 и индексы uint32. Позиции — мм относительно точки коллизии: они считаются
///     в double от абсолютных координат, поэтому float не теряет точности вдали от начала общей системы.
/// </summary>
public static class ViewerMeshWriter
{
    public const uint Magic = 0x31564354; // "TCV1"

    public static byte[] Header(int count)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), count);
        return bytes;
    }

    /// <param name="x">Точка коллизии в общей системе, мм.</param>
    public static byte[] Encode(int elementIndex, ElementMesh mesh, ModelHeader model, double x, double y, double z)
    {
        var positions = mesh.Positions;
        var indices = mesh.Indices;
        var bytes = new byte[12 + positions.Length * 4 + indices.Length * 4];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(span, elementIndex);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], positions.Length / 3);
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], indices.Length);
        var at = 12;
        double dx = model.OriginX - x, dy = model.OriginY - y, dz = model.OriginZ - z;
        for (var i = 0; i + 2 < positions.Length; i += 3)
        {
            BinaryPrimitives.WriteSingleLittleEndian(span[at..], (float)(positions[i] + dx));
            BinaryPrimitives.WriteSingleLittleEndian(span[(at + 4)..], (float)(positions[i + 1] + dy));
            BinaryPrimitives.WriteSingleLittleEndian(span[(at + 8)..], (float)(positions[i + 2] + dz));
            at += 12;
        }

        foreach (var index in indices)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span[at..], (uint)index);
            at += 4;
        }

        return bytes;
    }
}
