using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace CarCombat
{
    /// <summary>
    /// Читает Assets/StreamingAssets/car.vox. Пустая кайма отрезается, размер машины равен занятым клеткам.
    /// Ось MagicaVoxel: X вправо, Y от кормы к носу, Z вверх.
    /// </summary>
    public static class VoxBlueprint
    {
        public struct Cell
        {
            public int x;
            public int y;
            public int z;
            public char mark;
        }

        const int CellLimit = 2000000;

        static readonly uint[] DefaultPalette =
        {
            0x00000000, 0xffffffff, 0xffccffff, 0xff99ffff, 0xff66ffff, 0xff33ffff, 0xff00ffff, 0xffffccff, 0xffccccff, 0xff99ccff, 0xff66ccff, 0xff33ccff, 0xff00ccff, 0xffff99ff, 0xffcc99ff, 0xff9999ff,
            0xff6699ff, 0xff3399ff, 0xff0099ff, 0xffff66ff, 0xffcc66ff, 0xff9966ff, 0xff6666ff, 0xff3366ff, 0xff0066ff, 0xffff33ff, 0xffcc33ff, 0xff9933ff, 0xff6633ff, 0xff3333ff, 0xff0033ff, 0xffff00ff,
            0xffcc00ff, 0xff9900ff, 0xff6600ff, 0xff3300ff, 0xff0000ff, 0xffffffcc, 0xffccffcc, 0xff99ffcc, 0xff66ffcc, 0xff33ffcc, 0xff00ffcc, 0xffffcccc, 0xffcccccc, 0xff99cccc, 0xff66cccc, 0xff33cccc,
            0xff00cccc, 0xffff99cc, 0xffcc99cc, 0xff9999cc, 0xff6699cc, 0xff3399cc, 0xff0099cc, 0xffff66cc, 0xffcc66cc, 0xff9966cc, 0xff6666cc, 0xff3366cc, 0xff0066cc, 0xffff33cc, 0xffcc33cc, 0xff9933cc,
            0xff6633cc, 0xff3333cc, 0xff0033cc, 0xffff00cc, 0xffcc00cc, 0xff9900cc, 0xff6600cc, 0xff3300cc, 0xff0000cc, 0xffffff99, 0xffccff99, 0xff99ff99, 0xff66ff99, 0xff33ff99, 0xff00ff99, 0xffffcc99,
            0xffcccc99, 0xff99cc99, 0xff66cc99, 0xff33cc99, 0xff00cc99, 0xffff9999, 0xffcc9999, 0xff999999, 0xff669999, 0xff339999, 0xff009999, 0xffff6699, 0xffcc6699, 0xff996699, 0xff666699, 0xff336699,
            0xff006699, 0xffff3399, 0xffcc3399, 0xff993399, 0xff663399, 0xff333399, 0xff003399, 0xffff0099, 0xffcc0099, 0xff990099, 0xff660099, 0xff330099, 0xff000099, 0xffffff66, 0xffccff66, 0xff99ff66,
            0xff66ff66, 0xff33ff66, 0xff00ff66, 0xffffcc66, 0xffcccc66, 0xff99cc66, 0xff66cc66, 0xff33cc66, 0xff00cc66, 0xffff9966, 0xffcc9966, 0xff999966, 0xff669966, 0xff339966, 0xff009966, 0xffff6666,
            0xffcc6666, 0xff996666, 0xff666666, 0xff336666, 0xff006666, 0xffff3366, 0xffcc3366, 0xff993366, 0xff663366, 0xff333366, 0xff003366, 0xffff0066, 0xffcc0066, 0xff990066, 0xff660066, 0xff330066,
            0xff000066, 0xffffff33, 0xffccff33, 0xff99ff33, 0xff66ff33, 0xff33ff33, 0xff00ff33, 0xffffcc33, 0xffcccc33, 0xff99cc33, 0xff66cc33, 0xff33cc33, 0xff00cc33, 0xffff9933, 0xffcc9933, 0xff999933,
            0xff669933, 0xff339933, 0xff009933, 0xffff6633, 0xffcc6633, 0xff996633, 0xff666633, 0xff336633, 0xff006633, 0xffff3333, 0xffcc3333, 0xff993333, 0xff663333, 0xff333333, 0xff003333, 0xffff0033,
            0xffcc0033, 0xff990033, 0xff660033, 0xff330033, 0xff000033, 0xffffff00, 0xffccff00, 0xff99ff00, 0xff66ff00, 0xff33ff00, 0xff00ff00, 0xffffcc00, 0xffcccc00, 0xff99cc00, 0xff66cc00, 0xff33cc00,
            0xff00cc00, 0xffff9900, 0xffcc9900, 0xff999900, 0xff669900, 0xff339900, 0xff009900, 0xffff6600, 0xffcc6600, 0xff996600, 0xff666600, 0xff336600, 0xff006600, 0xffff3300, 0xffcc3300, 0xff993300,
            0xff663300, 0xff333300, 0xff003300, 0xffff0000, 0xffcc0000, 0xff990000, 0xff660000, 0xff330000, 0xff0000ee, 0xff0000dd, 0xff0000bb, 0xff0000aa, 0xff000088, 0xff000077, 0xff000055, 0xff000044,
            0xff000022, 0xff000011, 0xff00ee00, 0xff00dd00, 0xff00bb00, 0xff00aa00, 0xff008800, 0xff007700, 0xff005500, 0xff004400, 0xff002200, 0xff001100, 0xffee0000, 0xffdd0000, 0xffbb0000, 0xffaa0000,
            0xff880000, 0xff770000, 0xff550000, 0xff440000, 0xff220000, 0xff110000, 0xffeeeeee, 0xffdddddd, 0xffbbbbbb, 0xffaaaaaa, 0xff888888, 0xff777777, 0xff555555, 0xff444444, 0xff222222, 0xff111111
        };

        public static bool TryLoad(out int sizeX, out int sizeY, out int sizeZ, out Cell[] cells)
        {
            sizeX = 0;
            sizeY = 0;
            sizeZ = 0;
            cells = Array.Empty<Cell>();
            string path = Path.Combine(Application.streamingAssetsPath, "car.vox");
            if (!File.Exists(path))
                return false;

            try
            {
                byte[] data = File.ReadAllBytes(path);
                if (data.Length < 8 || data[0] != (byte)'V' || data[1] != (byte)'O' || data[2] != (byte)'X' || data[3] != (byte)' ')
                {
                    Debug.LogWarning("car.vox не похож на файл MagicaVoxel.");
                    return false;
                }

                var palette = (uint[])DefaultPalette.Clone();
                var raw = new List<Raw>(1024);
                int cursor = 8;
                Walk(data, ref cursor, data.Length, palette, raw);
                if (raw.Count == 0)
                {
                    Debug.LogWarning("В car.vox нет кубиков.");
                    return false;
                }

                int minX = int.MaxValue;
                int minY = int.MaxValue;
                int minZ = int.MaxValue;
                int maxX = int.MinValue;
                int maxY = int.MinValue;
                int maxZ = int.MinValue;
                for (int i = 0; i < raw.Count; i++)
                {
                    var item = raw[i];
                    if (item.x < minX) minX = item.x;
                    if (item.y < minY) minY = item.y;
                    if (item.z < minZ) minZ = item.z;
                    if (item.x > maxX) maxX = item.x;
                    if (item.y > maxY) maxY = item.y;
                    if (item.z > maxZ) maxZ = item.z;
                }

                int width = maxX - minX + 1;
                int length = maxY - minY + 1;
                int height = maxZ - minZ + 1;
                long volume = (long)width * height * length;
                if (width < 1 || height < 1 || length < 1 || volume > CellLimit)
                {
                    Debug.LogWarning("car.vox слишком большой для расчёта: " + width + "×" + height + "×" + length + ".");
                    return false;
                }

                cells = new Cell[raw.Count];
                for (int i = 0; i < raw.Count; i++)
                {
                    var item = raw[i];
                    uint color = palette[item.color & 255];
                    cells[i] = new Cell
                    {
                        x = item.x - minX,
                        y = item.z - minZ,
                        z = item.y - minY,
                        mark = MarkOf((byte)(color & 255), (byte)((color >> 8) & 255), (byte)((color >> 16) & 255))
                    };
                }

                sizeX = width;
                sizeY = height;
                sizeZ = length;
                Debug.Log("Машина из car.vox: " + width + "×" + height + "×" + length + ", кубиков " + cells.Length + ".");
                return true;
            }
            catch (Exception error)
            {
                Debug.LogWarning("car.vox не прочитан: " + error.Message);
                return false;
            }
        }

        struct Raw
        {
            public int x;
            public int y;
            public int z;
            public byte color;
        }

        static void Walk(byte[] data, ref int cursor, int end, uint[] palette, List<Raw> raw)
        {
            bool captured = raw.Count > 0;
            while (cursor + 12 <= end)
            {
                int content = ReadInt(data, cursor + 4);
                int children = ReadInt(data, cursor + 8);
                if (content < 0 || children < 0)
                    return;
                int contentAt = cursor + 12;
                long nextLong = (long)contentAt + content + children;
                if (nextLong > end)
                    return;
                int next = (int)nextLong;
                string id = "" + (char)data[cursor] + (char)data[cursor + 1] + (char)data[cursor + 2] + (char)data[cursor + 3];
                if (id == "XYZI" && content >= 4 && !captured)
                {
                    int count = ReadInt(data, contentAt);
                    int body = contentAt + 4;
                    if (count > 0 && body + count * 4 <= contentAt + content)
                    {
                        int before = raw.Count;
                        for (int i = 0; i < count; i++)
                        {
                            int at = body + i * 4;
                            byte colorIndex = data[at + 3];
                            if (colorIndex == 0)
                                continue;
                            raw.Add(new Raw
                            {
                                x = data[at],
                                y = data[at + 1],
                                z = data[at + 2],
                                color = colorIndex
                            });
                        }

                        captured = raw.Count > before;
                    }
                }
                else if (id == "RGBA" && content >= 1024)
                {
                    for (int i = 0; i <= 254; i++)
                    {
                        int at = contentAt + i * 4;
                        byte r = data[at];
                        byte g = data[at + 1];
                        byte b = data[at + 2];
                        byte a = data[at + 3];
                        palette[i + 1] = (uint)(r | (g << 8) | (b << 16) | (a << 24));
                    }
                }

                if (children > 0)
                {
                    int child = contentAt + content;
                    Walk(data, ref child, next, palette, raw);
                    captured = raw.Count > 0;
                }

                cursor = next;
            }
        }

        static int ReadInt(byte[] data, int at)
        {
            return data[at] | (data[at + 1] << 8) | (data[at + 2] << 16) | (data[at + 3] << 24);
        }

        static char MarkOf(byte red, byte green, byte blue)
        {
            float r = red / 255f;
            float g = green / 255f;
            float b = blue / 255f;
            float max = r > g ? r : g;
            if (b > max)
                max = b;
            float min = r < g ? r : g;
            if (b < min)
                min = b;
            float sat = max <= 0.001f ? 0f : (max - min) / max;
            if (max < 0.14f)
                return 'W';
            if (sat < 0.18f)
                return max < 0.48f ? 'A' : 'a';
            if (b > r && b >= g && b > 0.35f)
                return 'g';
            if (g > r + 0.05f && g > b)
                return 'T';
            if (r > 0.35f && g > 0.18f && r > b * 1.35f && g > b)
                return r + g < 1.15f || max < 0.62f ? 'E' : 'e';
            if (r > g && r > b)
                return 'G';
            return 'a';
        }
    }
}
