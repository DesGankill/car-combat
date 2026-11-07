using UnityEngine;

namespace CarCombat
{
    /// <summary>
    /// Аналог репликации акторов из практической №5.
    /// База шлёт всех каждый тик полными float. Оптимизация: сон, толчок только грязных,
    /// квантование, сетка релевантности и взгляд, реже у фона и чаще у опасных.
    /// </summary>
    public static class ReplicationBudget
    {
        public const int PropCount = 64;
        public const int CoverCount = 40;
        public const int PickupCount = 12;
        public const int HazardCount = 12;
        public const int Ticks = 100;
        public const int TickHz = 20;
        public const float Seconds = 5f;
        public const float CellSize = 10f;
        public const float CloseRadius = 8f;
        public const int HeaderBytes = 7;
        public const int FullRecordBytes = 23;
        public const int QuantRecordBytes = 10;

        public enum Kind : byte
        {
            Cover = 0,
            Pickup = 1,
            Hazard = 2
        }

        public struct Prop
        {
            public Kind kind;
            public float homeX;
            public float homeZ;
        }

        public struct Report
        {
            public int props;
            public int baselineBytes;
            public int optimizedBytes;
            public int baselineUpdates;
            public int optimizedUpdates;
            public float baselineKbPerSec;
            public float optimizedKbPerSec;
            public float reductionPercent;
        }

        public static Prop[] Build()
        {
            var props = new Prop[PropCount];
            for (int i = 0; i < CoverCount; i++)
            {
                props[i].kind = Kind.Cover;
                props[i].homeX = ((i % 8) - 3.5f) * 6f;
                props[i].homeZ = ((i / 8) - 2f) * 8f;
            }

            for (int i = 0; i < PickupCount; i++)
            {
                int id = CoverCount + i;
                props[id].kind = Kind.Pickup;
                if (i < 2)
                {
                    props[id].homeX = i == 0 ? 2f : -2f;
                    props[id].homeZ = i == 0 ? 3f : 4f;
                }
                else
                {
                    float angle = (i - 2) * 36f * Mathf.Deg2Rad;
                    props[id].homeX = Mathf.Cos(angle) * 28f;
                    props[id].homeZ = Mathf.Sin(angle) * 28f;
                }
            }

            float[] hazardX = { 0f, 4f, -4f, 2f, 0f, 3f, 30f, -30f, 0f, 0f, 24f, -24f };
            float[] hazardZ = { 6f, 8f, 8f, 12f, -5f, -4f, 0f, 0f, 30f, -30f, 24f, -24f };
            for (int i = 0; i < HazardCount; i++)
            {
                int id = CoverCount + PickupCount + i;
                props[id].kind = Kind.Hazard;
                props[id].homeX = hazardX[i];
                props[id].homeZ = hazardZ[i];
            }

            return props;
        }

        public static void Pose(Prop prop, int tick, out float x, out float z, out float yaw, out float vx, out float vz)
        {
            x = prop.homeX;
            z = prop.homeZ;
            yaw = 0f;
            vx = 0f;
            vz = 0f;
            if (prop.kind != Kind.Hazard)
                return;
            float phase = tick / (float)TickHz * 1.4f;
            vx = Mathf.Cos(phase) * 1.5f;
            x += Mathf.Sin(phase) * 1.5f;
        }

        public static bool IsRelevant(float x, float z, float originX, float originZ, float yawDeg)
        {
            float dx = x - originX;
            float dz = z - originZ;
            if (dx * dx + dz * dz <= CloseRadius * CloseRadius)
                return true;
            int cellX = Mathf.FloorToInt(x / CellSize);
            int cellZ = Mathf.FloorToInt(z / CellSize);
            int originCellX = Mathf.FloorToInt(originX / CellSize);
            int originCellZ = Mathf.FloorToInt(originZ / CellSize);
            if (Mathf.Abs(cellX - originCellX) > 1 || Mathf.Abs(cellZ - originCellZ) > 1)
                return false;
            float rad = yawDeg * Mathf.Deg2Rad;
            return dx * Mathf.Sin(rad) + dz * Mathf.Cos(rad) > 0f;
        }

        public static int Collect(Prop[] props, int tick, float originX, float originZ, float yawDeg, bool optimized, int[] into)
        {
            int count = 0;
            for (int i = 0; i < props.Length; i++)
            {
                if (optimized && !Include(props[i], i, tick, originX, originZ, yawDeg))
                    continue;
                if (count < into.Length)
                    into[count] = i;
                count++;
            }

            return count;
        }

        public static Report Measure()
        {
            var props = Build();
            var ids = new int[PropCount];
            int baselineBytes = 0;
            int optimizedBytes = 0;
            int baselineUpdates = 0;
            int optimizedUpdates = 0;
            for (int tick = 0; tick < Ticks; tick++)
            {
                int all = Collect(props, tick, 0f, 0f, 0f, false, ids);
                baselineUpdates += all;
                baselineBytes += HeaderBytes + all * FullRecordBytes;
                int kept = Collect(props, tick, 0f, 0f, 0f, true, ids);
                optimizedUpdates += kept;
                if (kept > 0)
                    optimizedBytes += HeaderBytes + kept * QuantRecordBytes;
            }

            float baselinePerSec = baselineBytes / Seconds;
            float optimizedPerSec = optimizedBytes / Seconds;
            return new Report
            {
                props = PropCount,
                baselineBytes = baselineBytes,
                optimizedBytes = optimizedBytes,
                baselineUpdates = baselineUpdates,
                optimizedUpdates = optimizedUpdates,
                baselineKbPerSec = baselinePerSec / 1024f,
                optimizedKbPerSec = optimizedPerSec / 1024f,
                reductionPercent = baselineBytes == 0 ? 0f : (baselineBytes - optimizedBytes) * 100f / baselineBytes
            };
        }

        static bool Include(Prop prop, int id, int tick, float originX, float originZ, float yawDeg)
        {
            if (tick == 0)
                return true;
            if (prop.kind == Kind.Cover)
                return false;
            if (prop.kind == Kind.Pickup && !((id == 40 && tick == 20) || (id == 41 && tick == 60)))
                return false;
            if (prop.kind == Kind.Hazard && (tick & 1) != 0)
                return false;
            Pose(prop, tick, out float x, out float z, out _, out _, out _);
            return IsRelevant(x, z, originX, originZ, yawDeg);
        }
    }
}
