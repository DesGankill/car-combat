using System;
using Unity.Netcode;
using UnityEngine;

namespace CarCombat
{
    /// <summary>
    /// Команда игрока. Позиции здесь нет: сервер не должен принимать координаты клиента.
    /// turret — мировой курс ствола в градусах. pitch — наклон ствола, выше или ниже горизонта.
    /// </summary>
    [Serializable]
    public struct DriveInput : INetworkSerializable, IEquatable<DriveInput>
    {
        public int tick;
        public int throttle;
        public int steer;
        public int turret;
        public int pitch;
        public int shell;
        public bool snapAim;
        public bool fire;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref tick);
            serializer.SerializeValue(ref throttle);
            serializer.SerializeValue(ref steer);
            serializer.SerializeValue(ref turret);
            serializer.SerializeValue(ref pitch);
            serializer.SerializeValue(ref shell);
            serializer.SerializeValue(ref snapAim);
            serializer.SerializeValue(ref fire);
        }

        public bool Equals(DriveInput other)
        {
            return tick == other.tick && throttle == other.throttle && steer == other.steer && turret == other.turret && pitch == other.pitch && shell == other.shell && snapAim == other.snapAim && fire == other.fire;
        }
    }

    /// <summary>
    /// Поза машины, которую считает сервер. Клиент получает её целиком и не имеет права её назначать.
    /// </summary>
    [Serializable]
    public struct CarState : INetworkSerializable, IEquatable<CarState>
    {
        public int tick;
        public float x;
        public float z;
        public float yaw;
        public float turret;
        public float pitch;
        public float speed;
        public float cooldown;
        public VoxelMask blocks;
        public int lastShotTick;
        public int lastShell;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref tick);
            serializer.SerializeValue(ref x);
            serializer.SerializeValue(ref z);
            serializer.SerializeValue(ref yaw);
            serializer.SerializeValue(ref turret);
            serializer.SerializeValue(ref pitch);
            serializer.SerializeValue(ref speed);
            serializer.SerializeValue(ref cooldown);
            serializer.SerializeValue(ref blocks);
            serializer.SerializeValue(ref lastShotTick);
            serializer.SerializeValue(ref lastShell);
        }

        public bool Equals(CarState other)
        {
            return tick == other.tick
                && x.Equals(other.x)
                && z.Equals(other.z)
                && yaw.Equals(other.yaw)
                && turret.Equals(other.turret)
                && pitch.Equals(other.pitch)
                && speed.Equals(other.speed)
                && cooldown.Equals(other.cooldown)
                && blocks.Equals(other.blocks)
                && lastShotTick == other.lastShotTick
                && lastShell == other.lastShell;
        }

        public override bool Equals(object obj)
        {
            return obj is CarState other && Equals(other);
        }

        public override int GetHashCode()
        {
            return tick ^ (blocks.GetHashCode() << 1) ^ lastShotTick;
        }
    }

    /// <summary>
    /// Одна и та же функция шага для клиента и сервера.
    /// Аналог того, что в Unreal делает CharacterMovementComponent: ввод плюс состояние дают следующее состояние.
    /// </summary>
    public static class CarSim
    {
        public const float TickDt = 1f / 30f;
        public const float Accel = 28f;
        public const float Brake = 36f;
        public const float Drag = 8f;
        public const float MaxSpeed = 13f;
        public const float TurnRate = 160f;
        public const float TurretRate = 420f;
        public const float PitchRate = 280f;
        public const float PitchMin = -40f;
        public const float PitchMax = 55f;
        public const float MuzzleSpeed = 42f;
        public const float Gravity = 20f;
        public const float BulletStep = 0.02f;
        public const float ShotCooldown = 0.35f;
        public const float Cell = 0.32f;
        public const float MuzzleHeight = Cell * 1.5f;

        public static float ShotRange { get; private set; } = 18f;
        public static float ArenaBound { get; private set; } = 16f;
        public static int GridX { get; private set; }
        public static int GridY { get; private set; }
        public static int GridZ { get; private set; }
        public static int BlockCount { get; private set; }
        public static float HalfW { get; private set; }
        public static float HalfL { get; private set; }

        // Запасная машина, если рядом нет car.vox. Слои снизу вверх, ряды от кормы к носу.
        // W колесо, a лёгкая броня, A тяжёлая, e/E двигатели, T поворот пушки, G пушка, g стекло.
        const int ShapeY = 4;
        const int ShapeZ = 8;
        static readonly string[] Shape =
        {
            "WW.WW", "aaaaa", "aaaaa", "aaaaa", "aaaaa", "aaaaa", "aaaaa", "WW.WW",
            "aaaaa", "aeEea", "aaaaa", "AAAAA", "AAAAA", "aaaaa", "aaaaa", "aaaaa",
            ".....", ".....", ".TTT.", ".TGT.", ".ggg.", ".....", ".....", ".....",
            ".....", ".....", "..T..", "..G..", ".....", ".....", ".....", "....."
        };

        public static VoxelMask Blueprint { get; private set; }
        public static int BlueprintCount { get; private set; }
        public static Vector3 TurretPivot { get; private set; }
        static VoxelRole[] Roles = Array.Empty<VoxelRole>();
        static byte[] MaxHpOf = Array.Empty<byte>();
        static int[] GroupOf = Array.Empty<int>();
        static float[] MassOf = Array.Empty<float>();
        static float[] PowerOf = Array.Empty<float>();
        static int[] WheelRow = Array.Empty<int>();
        static int[] axleTotal = Array.Empty<int>();
        static int[] axleAlive = Array.Empty<int>();
        static int steerAxle;
        static float freshMass = 1f;
        static float freshPower = 1f;

        static CarSim()
        {
            if (VoxBlueprint.TryLoad(out int sizeX, out int sizeY, out int sizeZ, out var cells))
                LoadVox(sizeX, sizeY, sizeZ, cells);
            else
                LoadShape();
        }

        static void LoadShape()
        {
            BeginGrid(Shape[0].Length, ShapeY, ShapeZ);
            var mask = VoxelMask.Allocate(BlockCount);
            for (int iy = 0; iy < GridY; iy++)
            {
                for (int iz = 0; iz < GridZ; iz++)
                {
                    string line = Shape[iy * GridZ + iz];
                    int width = line.Length < GridX ? line.Length : GridX;
                    for (int ix = 0; ix < width; ix++)
                        Stamp(line[ix], Index(ix, iy, iz), iz, ref mask);
                }
            }

            Finish(mask);
        }

        static void LoadVox(int sizeX, int sizeY, int sizeZ, VoxBlueprint.Cell[] cells)
        {
            BeginGrid(sizeX, sizeY, sizeZ);
            var mask = VoxelMask.Allocate(BlockCount);
            for (int i = 0; i < cells.Length; i++)
            {
                var cell = cells[i];
                if (cell.x < 0 || cell.y < 0 || cell.z < 0 || cell.x >= GridX || cell.y >= GridY || cell.z >= GridZ)
                    continue;
                Stamp(cell.mark, Index(cell.x, cell.y, cell.z), cell.z, ref mask);
            }

            Finish(mask);
        }

        static void BeginGrid(int sizeX, int sizeY, int sizeZ)
        {
            GridX = sizeX < 1 ? 1 : sizeX;
            GridY = sizeY < 1 ? 1 : sizeY;
            GridZ = sizeZ < 1 ? 1 : sizeZ;
            BlockCount = GridX * GridY * GridZ;
            HalfW = GridX * Cell * 0.5f;
            HalfL = GridZ * Cell * 0.5f;
            float reach = HalfW > HalfL ? HalfW : HalfL;
            ArenaBound = Mathf.Max(16f, reach * 4f + 8f);
            ShotRange = Mathf.Max(18f, (HalfW + HalfL) * 6f);
            Roles = new VoxelRole[BlockCount];
            MaxHpOf = new byte[BlockCount];
            GroupOf = new int[BlockCount];
            MassOf = new float[BlockCount];
            PowerOf = new float[BlockCount];
            WheelRow = new int[BlockCount];
            for (int i = 0; i < BlockCount; i++)
                WheelRow[i] = -1;
        }

        static void Finish(VoxelMask mask)
        {
            AssignAxles();
            var pivot = Vector3.zero;
            int pivotCount = 0;
            for (int i = 0; i < BlockCount; i++)
            {
                if (Roles[i] != VoxelRole.Gun && Roles[i] != VoxelRole.Turret)
                    continue;
                CellCenter(i, out float x, out float y, out float z);
                pivot += new Vector3(x, y, z);
                pivotCount++;
            }

            Blueprint = mask;
            BlueprintCount = Count(mask);
            TurretPivot = pivotCount > 0 ? pivot / pivotCount : new Vector3(0f, Cell * 2f, 0f);
            var fresh = Evaluate(mask);
            freshMass = Mathf.Max(fresh.mass, 0.05f);
            freshPower = Mathf.Max(fresh.power, 0.01f);
        }

        static void AssignAxles()
        {
            var rowGroup = new int[GridZ];
            int rows = 0;
            for (int iz = 0; iz < GridZ; iz++)
            {
                bool hasWheel = false;
                for (int i = 0; i < BlockCount; i++)
                {
                    if (WheelRow[i] != iz)
                        continue;
                    hasWheel = true;
                    break;
                }

                if (!hasWheel)
                    continue;
                rows++;
                rowGroup[iz] = rows;
            }

            axleTotal = new int[rows + 1];
            axleAlive = new int[rows + 1];
            steerAxle = rows;
            for (int i = 0; i < BlockCount; i++)
            {
                int iz = WheelRow[i];
                if (iz < 0)
                    continue;
                int group = rowGroup[iz];
                GroupOf[i] = group;
                axleTotal[group]++;
            }
        }

        static bool Stamp(char mark, int index, int iz, ref VoxelMask mask)
        {
            switch (mark)
            {
                case 'W':
                    mask.Write(index, 1);
                    Roles[index] = VoxelRole.Wheel;
                    MassOf[index] = 0.12f;
                    WheelRow[index] = iz;
                    break;
                case 'a':
                    mask.Write(index, 2);
                    Roles[index] = VoxelRole.Armor;
                    MassOf[index] = 0.22f;
                    break;
                case 'A':
                    mask.Write(index, 5);
                    Roles[index] = VoxelRole.Armor;
                    MassOf[index] = 0.55f;
                    break;
                case 'e':
                    mask.Write(index, 3);
                    Roles[index] = VoxelRole.Engine;
                    MassOf[index] = 0.4f;
                    PowerOf[index] = 90f;
                    break;
                case 'E':
                    mask.Write(index, 5);
                    Roles[index] = VoxelRole.Engine;
                    MassOf[index] = 0.85f;
                    PowerOf[index] = 210f;
                    break;
                case 'G':
                    mask.Write(index, 3);
                    Roles[index] = VoxelRole.Gun;
                    MassOf[index] = 0.3f;
                    break;
                case 'T':
                    mask.Write(index, 2);
                    Roles[index] = VoxelRole.Turret;
                    MassOf[index] = 0.16f;
                    break;
                case 'g':
                    mask.Write(index, 1);
                    Roles[index] = VoxelRole.Glass;
                    MassOf[index] = 0.05f;
                    break;
                default:
                    return false;
            }

            MaxHpOf[index] = (byte)mask.Get(index);
            return true;
        }

        public static CarState Spawn(int slot)
        {
            float gap = Mathf.Max(7f, HalfL + HalfW + 1.5f);
            float x;
            float z;
            float yaw;
            switch (slot & 3)
            {
                case 1:
                    x = 0f;
                    z = gap;
                    yaw = 180f;
                    break;
                case 2:
                    x = -gap;
                    z = 0f;
                    yaw = 90f;
                    break;
                case 3:
                    x = gap;
                    z = 0f;
                    yaw = -90f;
                    break;
                default:
                    x = 0f;
                    z = -gap;
                    yaw = 0f;
                    break;
            }

            return new CarState
            {
                tick = 0,
                x = x,
                z = z,
                yaw = yaw,
                speed = 0f,
                cooldown = 0f,
                blocks = Blueprint,
                lastShotTick = 0
            };
        }

        public static int Index(int ix, int iy, int iz)
        {
            return ix + GridX * (iy + GridY * iz);
        }

        public static void CellOf(int index, out int ix, out int iy, out int iz)
        {
            ix = index % GridX;
            int rest = index / GridX;
            iy = rest % GridY;
            iz = rest / GridY;
        }

        public static void CellCenter(int index, out float x, out float y, out float z)
        {
            CellOf(index, out int ix, out int iy, out int iz);
            x = (ix + 0.5f) * Cell - HalfW;
            y = (iy + 0.5f) * Cell;
            z = (iz + 0.5f) * Cell - HalfL;
        }

        public static VoxelRole RoleOf(int index)
        {
            if ((uint)index >= (uint)Roles.Length)
                return VoxelRole.None;
            return Roles[index];
        }

        public static int Count(VoxelMask mask)
        {
            int count = 0;
            for (int i = 0; i < BlockCount; i++)
            {
                if (mask.IsSet(i))
                    count++;
            }

            return count;
        }

        public static int MaxHp(int index)
        {
            if ((uint)index >= (uint)MaxHpOf.Length)
                return 1;
            return MaxHpOf[index];
        }

        public static int TotalHp(VoxelMask mask)
        {
            int total = 0;
            for (int i = 0; i < BlockCount; i++)
                total += mask.Get(i);
            return total;
        }

        public static PartStats Evaluate(VoxelMask mask)
        {
            for (int g = 0; g < axleAlive.Length; g++)
                axleAlive[g] = 0;

            float mass = 0f;
            float power = 0f;
            int guns = 0;
            int turrets = 0;
            for (int i = 0; i < BlockCount; i++)
            {
                if (mask.Get(i) <= 0)
                    continue;
                mass += MassOf[i];
                power += PowerOf[i];
                int group = GroupOf[i];
                if (group > 0)
                    axleAlive[group]++;
                if (Roles[i] == VoxelRole.Gun)
                    guns++;
                if (Roles[i] == VoxelRole.Turret)
                    turrets++;
            }

            float drive = 0f;
            int axles = 0;
            for (int g = 1; g < axleTotal.Length; g++)
            {
                if (axleTotal[g] <= 0)
                    continue;
                drive += axleAlive[g] / (float)axleTotal[g];
                axles++;
            }

            if (axles > 0)
                drive /= axles;

            return new PartStats
            {
                mass = Mathf.Max(mass, 0.05f),
                power = power,
                drive = drive,
                canSteer = steerAxle > 0 && axleAlive[steerAxle] > 0,
                canFire = guns > 0,
                canAim = turrets > 0
            };
        }

        public static CarState Apply(CarState state, DriveInput input, bool resolveFire)
        {
            bool canFire = resolveFire && input.fire && state.cooldown <= 0.0001f && Evaluate(state.blocks).canFire;
            state = Integrate(state, input);
            if (canFire)
            {
                state.cooldown = ShotCooldown;
                state.lastShotTick = input.tick;
                state.lastShell = input.shell;
            }

            return state;
        }

        public static CarState Integrate(CarState state, DriveInput input)
        {
            var stats = Evaluate(state.blocks);
            int throttle = input.throttle;
            int steer = input.steer;
            if (stats.power <= 0.01f || stats.drive <= 0.001f)
                throttle = 0;
            if (!stats.canSteer)
                steer = 0;

            float scale = (stats.power / freshPower) * (freshMass / stats.mass) * stats.drive;
            if (throttle > 0)
                state.speed += Accel * scale * TickDt;
            else if (throttle < 0)
                state.speed -= Brake * scale * TickDt;
            else
                state.speed = Mathf.MoveTowards(state.speed, 0f, Drag * TickDt);

            float maxSpeed = MaxSpeed * Mathf.Sqrt(Mathf.Max(scale, 0f));
            state.speed = Mathf.Clamp(state.speed, -maxSpeed * 0.4f, maxSpeed);

            float yawBefore = state.yaw;
            float turn = steer * TurnRate * TickDt * Mathf.Clamp01(Mathf.Abs(state.speed) / 3.5f);
            if (state.speed < 0f)
                turn = -turn;
            state.yaw += turn;

            float rad = state.yaw * Mathf.Deg2Rad;
            state.x += Mathf.Sin(rad) * state.speed * TickDt;
            state.z += Mathf.Cos(rad) * state.speed * TickDt;

            bool hitWall = false;
            if (state.x > ArenaBound)
            {
                state.x = ArenaBound;
                hitWall = true;
            }
            else if (state.x < -ArenaBound)
            {
                state.x = -ArenaBound;
                hitWall = true;
            }

            if (state.z > ArenaBound)
            {
                state.z = ArenaBound;
                hitWall = true;
            }
            else if (state.z < -ArenaBound)
            {
                state.z = -ArenaBound;
                hitWall = true;
            }

            if (hitWall)
                state.speed = 0f;

            if (stats.canAim)
            {
                float desired = Mathf.DeltaAngle(state.yaw, input.turret);
                if (input.snapAim)
                {
                    state.turret = Mathf.Repeat(desired + 180f, 360f) - 180f;
                    state.pitch = Mathf.Clamp(input.pitch, PitchMin, PitchMax);
                }
                else
                {
                    state.turret -= Mathf.DeltaAngle(yawBefore, state.yaw);
                    float step = Mathf.Clamp(Mathf.DeltaAngle(state.turret, desired), -TurretRate * TickDt, TurretRate * TickDt);
                    state.turret = Mathf.Repeat(state.turret + step + 180f, 360f) - 180f;
                    state.pitch = Mathf.MoveTowards(state.pitch, Mathf.Clamp(input.pitch, PitchMin, PitchMax), PitchRate * TickDt);
                }
            }

            state.cooldown = Mathf.Max(0f, state.cooldown - TickDt);
            state.tick = input.tick;
            return state;
        }

        public static bool TryHit(CarState shooter, CarState target, out int block)
        {
            return TryHit(shooter, target, out block, out _);
        }

        static readonly CarState[] oneTarget = new CarState[1];

        public static bool TryHit(CarState shooter, CarState target, out int block, out float distance)
        {
            oneTarget[0] = target;
            var hit = Fly(shooter, oneTarget, 1, null, 0, out _);
            block = hit.block;
            distance = hit.distance;
            return hit.car;
        }

        public static BallisticHit Fly(
            CarState shooter,
            CarState[] targets,
            int targetCount,
            Vector3[] samples,
            int capacity,
            out int written)
        {
            written = 0;
            var miss = new BallisticHit { block = -1, distance = ShotRange, point = new Vector3(shooter.x, 0f, shooter.z) };
            if (!Evaluate(shooter.blocks).canFire)
                return miss;

            Muzzle(shooter, out Vector3 origin, out Vector3 direction);
            var pos = origin + direction * (Cell * 0.65f);
            var velocity = direction * MuzzleSpeed;
            float traveled = Cell * 0.65f;
            int guard = 0;
            while (traveled < ShotRange && guard < 160)
            {
                guard++;
                var previous = pos;
                velocity.y -= Gravity * BulletStep;
                pos += velocity * BulletStep;
                float step = Vector3.Distance(previous, pos);
                if (step < 0.00001f)
                    break;

                float carT = 2f;
                int carBlock = -1;
                int carIndex = -1;
                int count = targets == null ? 0 : targetCount;
                for (int c = 0; c < count; c++)
                {
                    if (!SegmentCar(previous, pos, targets[c], out int block, out float t))
                        continue;
                    if (t >= carT)
                        continue;
                    carT = t;
                    carBlock = block;
                    carIndex = c;
                }

                float groundT = CrossDown(previous.y, pos.y, 0f);
                float wallT = CrossWall(previous, pos);
                float surfaceT = groundT < wallT ? groundT : wallT;
                bool hitCar = carT <= 1f && carT <= surfaceT;
                bool hitSurface = !hitCar && surfaceT <= 1f;
                if (hitCar || hitSurface)
                {
                    float t = hitCar ? carT : surfaceT;
                    var point = Vector3.Lerp(previous, pos, Mathf.Clamp01(t));
                    if (!hitCar && groundT <= wallT)
                        point.y = 0f;
                    WriteSample(samples, capacity, ref written, point);
                    return new BallisticHit
                    {
                        car = hitCar,
                        surface = hitSurface,
                        block = hitCar ? carBlock : -1,
                        point = point,
                        distance = traveled + step * Mathf.Clamp01(t),
                        target = carIndex
                    };
                }

                traveled += step;
                WriteSample(samples, capacity, ref written, pos);
            }

            miss.point = pos;
            miss.distance = traveled;
            return miss;
        }

        public static Vector3 TurretWorld(CarState state)
        {
            float yaw = state.yaw * Mathf.Deg2Rad;
            float sin = Mathf.Sin(yaw);
            float cos = Mathf.Cos(yaw);
            return new Vector3(
                state.x + TurretPivot.x * cos + TurretPivot.z * sin,
                TurretPivot.y,
                state.z - TurretPivot.x * sin + TurretPivot.z * cos);
        }

        public static void SolveAim(CarState state, Vector3 point, out int worldYaw, out int pitch)
        {
            float yaw = Mathf.Atan2(point.x - state.x, point.z - state.z) * Mathf.Rad2Deg;
            var probe = state;
            probe.turret = Mathf.DeltaAngle(state.yaw, yaw);
            probe.pitch = 0f;
            for (int i = 0; i < 2; i++)
            {
                Muzzle(probe, out Vector3 origin, out _);
                yaw = Mathf.Atan2(point.x - origin.x, point.z - origin.z) * Mathf.Rad2Deg;
                probe.turret = Mathf.DeltaAngle(probe.yaw, yaw);
                probe.pitch = Mathf.Clamp(BallisticPitch(origin, point), PitchMin, PitchMax);
            }

            worldYaw = Mathf.RoundToInt(Mathf.Repeat(yaw + 180f, 360f) - 180f);
            pitch = Mathf.RoundToInt(probe.pitch);
        }

        static float BallisticPitch(Vector3 origin, Vector3 point)
        {
            float dx = point.x - origin.x;
            float dz = point.z - origin.z;
            float distance = Mathf.Sqrt(dx * dx + dz * dz);
            float height = point.y - origin.y;
            if (distance < 0.2f)
                return height >= 0f ? PitchMax : PitchMin;

            float speed2 = MuzzleSpeed * MuzzleSpeed;
            float disc = speed2 * speed2 - Gravity * (Gravity * distance * distance + 2f * height * speed2);
            if (disc < 0f)
                return 45f;
            float tangent = (speed2 - Mathf.Sqrt(disc)) / (Gravity * distance);
            return Mathf.Atan(tangent) * Mathf.Rad2Deg;
        }

        public static int WorldYawDegrees(Vector3 pivot, Vector3 point, int fallback)
        {
            float dx = point.x - pivot.x;
            float dz = point.z - pivot.z;
            if (dx * dx + dz * dz < 0.0004f)
                return fallback;
            float yaw = Mathf.Atan2(dx, dz) * Mathf.Rad2Deg;
            return Mathf.RoundToInt(Mathf.Repeat(yaw + 180f, 360f) - 180f);
        }

        public static Vector3 BlockWorld(CarState car, int block)
        {
            CellCenter(block, out float x, out float y, out float z);
            float yaw = car.yaw * Mathf.Deg2Rad;
            float sin = Mathf.Sin(yaw);
            float cos = Mathf.Cos(yaw);
            return new Vector3(
                car.x + x * cos + z * sin,
                y,
                car.z - x * sin + z * cos);
        }

        public static void Muzzle(CarState shooter, out Vector3 origin, out Vector3 direction)
        {
            var spin = Quaternion.Euler(0f, shooter.turret, 0f) * Quaternion.Euler(-shooter.pitch, 0f, 0f);
            var hull = Quaternion.Euler(0f, shooter.yaw, 0f);
            direction = hull * spin * Vector3.forward;
            float bestZ = float.MinValue;
            var carLocal = new Vector3(0f, MuzzleHeight, HalfL * 0.4f);
            for (int i = 0; i < BlockCount; i++)
            {
                if (Roles[i] != VoxelRole.Gun || shooter.blocks.Get(i) <= 0)
                    continue;
                CellCenter(i, out float cellX, out float cellY, out float cellZ);
                if (cellZ < bestZ)
                    continue;
                bestZ = cellZ;
                carLocal = new Vector3(cellX, cellY, cellZ);
            }

            origin = hull * (spin * (carLocal - TurretPivot) + TurretPivot) + new Vector3(shooter.x, 0f, shooter.z);
        }

        static void WriteSample(Vector3[] samples, int capacity, ref int written, Vector3 point)
        {
            if (samples == null || written >= capacity || written >= samples.Length)
                return;
            samples[written++] = point;
        }

        static bool SegmentCar(Vector3 a, Vector3 b, CarState car, out int block, out float tHit)
        {
            block = -1;
            tHit = 2f;
            if (car.blocks.IsEmpty)
                return false;

            var localA = ToLocal(a, car);
            var localB = ToLocal(b, car);
            float minX = Mathf.Min(localA.x, localB.x);
            float maxX = Mathf.Max(localA.x, localB.x);
            float minY = Mathf.Min(localA.y, localB.y);
            float maxY = Mathf.Max(localA.y, localB.y);
            float minZ = Mathf.Min(localA.z, localB.z);
            float maxZ = Mathf.Max(localA.z, localB.z);
            if (maxX < -HalfW || minX > HalfW || maxZ < -HalfL || minZ > HalfL || maxY < 0f || minY > GridY * Cell)
                return false;

            int ix0 = Mathf.Clamp(Mathf.FloorToInt((minX + HalfW) / Cell) - 1, 0, GridX - 1);
            int ix1 = Mathf.Clamp(Mathf.FloorToInt((maxX + HalfW) / Cell) + 1, 0, GridX - 1);
            int iy0 = Mathf.Clamp(Mathf.FloorToInt(minY / Cell) - 1, 0, GridY - 1);
            int iy1 = Mathf.Clamp(Mathf.FloorToInt(maxY / Cell) + 1, 0, GridY - 1);
            int iz0 = Mathf.Clamp(Mathf.FloorToInt((minZ + HalfL) / Cell) - 1, 0, GridZ - 1);
            int iz1 = Mathf.Clamp(Mathf.FloorToInt((maxZ + HalfL) / Cell) + 1, 0, GridZ - 1);
            bool found = false;
            for (int iz = iz0; iz <= iz1; iz++)
            {
                for (int iy = iy0; iy <= iy1; iy++)
                {
                    for (int ix = ix0; ix <= ix1; ix++)
                    {
                        int index = Index(ix, iy, iz);
                        if (car.blocks.Get(index) <= 0)
                            continue;
                        var min = new Vector3(ix * Cell - HalfW, iy * Cell, iz * Cell - HalfL);
                        var max = min + new Vector3(Cell, Cell, Cell);
                        if (!SegmentAabb(localA, localB, min, max, out float t) || t >= tHit)
                            continue;
                        tHit = t;
                        block = index;
                        found = true;
                    }
                }
            }

            return found;
        }

        static Vector3 ToLocal(Vector3 world, CarState car)
        {
            WorldToLocal(world.x - car.x, world.z - car.z, car.yaw, out float x, out float z);
            return new Vector3(x, world.y, z);
        }

        static bool SegmentAabb(Vector3 a, Vector3 b, Vector3 min, Vector3 max, out float tHit)
        {
            var dir = b - a;
            float tMin = 0f;
            float tMax = 1f;
            tHit = 0f;
            if (!Slab(a.x, dir.x, min.x, max.x, ref tMin, ref tMax))
                return false;
            if (!Slab(a.y, dir.y, min.y, max.y, ref tMin, ref tMax))
                return false;
            if (!Slab(a.z, dir.z, min.z, max.z, ref tMin, ref tMax))
                return false;
            if (tMax < 0f || tMin > 1f)
                return false;
            tHit = Mathf.Clamp01(tMin);
            return true;
        }

        static float CrossDown(float fromY, float toY, float plane)
        {
            if (fromY <= plane)
                return 0f;
            if (toY > plane)
                return 2f;
            return (fromY - plane) / (fromY - toY);
        }

        static float CrossWall(Vector3 from, Vector3 to)
        {
            float best = 2f;
            best = Mathf.Min(best, CrossPlane(from.x, to.x, ArenaBound));
            best = Mathf.Min(best, CrossPlane(from.x, to.x, -ArenaBound));
            best = Mathf.Min(best, CrossPlane(from.z, to.z, ArenaBound));
            best = Mathf.Min(best, CrossPlane(from.z, to.z, -ArenaBound));
            return best;
        }

        static float CrossPlane(float from, float to, float plane)
        {
            if ((from - plane) * (to - plane) > 0f)
                return 2f;
            if (Mathf.Abs(to - from) < 0.00001f)
                return 2f;
            float t = (plane - from) / (to - from);
            return t >= 0f && t <= 1f ? t : 2f;
        }

        public static VoxelMask Damage(VoxelMask mask, int block)
        {
            return Damage(mask, block, 1);
        }

        public static VoxelMask Damage(VoxelMask mask, int block, int amount)
        {
            if (block < 0 || block >= BlockCount || amount <= 0)
                return mask;
            int hp = mask.Get(block);
            if (hp <= 0)
                return mask;
            return mask.Set(block, Mathf.Max(0, hp - amount));
        }

        public static VoxelMask ApplyShell(VoxelMask mask, CarState car, Vector3 impact, int shellIndex, int directBlock)
        {
            var shell = Solid.Of(shellIndex);
            if (shell.splashRadius > 0f && shell.splashDamage > 0)
            {
                float reach = shell.splashRadius * shell.splashRadius;
                for (int i = 0; i < BlockCount; i++)
                {
                    if (i == directBlock || mask.Get(i) <= 0)
                        continue;
                    var center = BlockWorld(car, i);
                    if ((center - impact).sqrMagnitude > reach)
                        continue;
                    mask = Damage(mask, i, shell.splashDamage);
                }
            }

            if (directBlock >= 0)
                mask = Damage(mask, directBlock, shell.directDamage);
            return mask;
        }

        public static CarState Collide(CarState self, CarState other)
        {
            if (self.blocks.IsEmpty || other.blocks.IsEmpty)
                return self;

            float dx = other.x - self.x;
            float dz = other.z - self.z;
            if (dx * dx + dz * dz < 0.000001f)
            {
                self.x -= 0.05f;
                return self;
            }

            Heading(self.yaw, out float sfx, out float sfz, out float srx, out float srz);
            Heading(other.yaw, out float ofx, out float ofz, out float orx, out float orz);
            float halfX = HalfW * 0.96f;
            float halfZ = HalfL * 0.96f;
            float best = float.MaxValue;
            float pushX = 0f;
            float pushZ = 0f;
            if (!PushAxis(dx, dz, sfx, sfz, halfX, halfZ, sfx, sfz, srx, srz, ofx, ofz, orx, orz, ref best, ref pushX, ref pushZ))
                return self;
            if (!PushAxis(dx, dz, srx, srz, halfX, halfZ, sfx, sfz, srx, srz, ofx, ofz, orx, orz, ref best, ref pushX, ref pushZ))
                return self;
            if (!PushAxis(dx, dz, ofx, ofz, halfX, halfZ, sfx, sfz, srx, srz, ofx, ofz, orx, orz, ref best, ref pushX, ref pushZ))
                return self;
            if (!PushAxis(dx, dz, orx, orz, halfX, halfZ, sfx, sfz, srx, srz, ofx, ofz, orx, orz, ref best, ref pushX, ref pushZ))
                return self;

            self.x += pushX;
            self.z += pushZ;
            float velocityX = Mathf.Sin(self.yaw * Mathf.Deg2Rad) * self.speed;
            float velocityZ = Mathf.Cos(self.yaw * Mathf.Deg2Rad) * self.speed;
            if (velocityX * pushX + velocityZ * pushZ < 0f)
                self.speed = 0f;
            return self;
        }

        public static void WorldToLocal(float dx, float dz, float yawDeg, out float localX, out float localZ)
        {
            float rad = yawDeg * Mathf.Deg2Rad;
            float sin = Mathf.Sin(rad);
            float cos = Mathf.Cos(rad);
            localX = dx * cos - dz * sin;
            localZ = dx * sin + dz * cos;
        }

        static void Heading(float yawDeg, out float forwardX, out float forwardZ, out float rightX, out float rightZ)
        {
            float rad = yawDeg * Mathf.Deg2Rad;
            float sin = Mathf.Sin(rad);
            float cos = Mathf.Cos(rad);
            forwardX = sin;
            forwardZ = cos;
            rightX = cos;
            rightZ = -sin;
        }

        static float Project(float axisX, float axisZ, float forwardX, float forwardZ, float rightX, float rightZ, float halfX, float halfZ)
        {
            return halfX * Mathf.Abs(rightX * axisX + rightZ * axisZ)
                + halfZ * Mathf.Abs(forwardX * axisX + forwardZ * axisZ);
        }

        static bool PushAxis(
            float dx,
            float dz,
            float axisX,
            float axisZ,
            float halfX,
            float halfZ,
            float sfx,
            float sfz,
            float srx,
            float srz,
            float ofx,
            float ofz,
            float orx,
            float orz,
            ref float best,
            ref float pushX,
            ref float pushZ)
        {
            float span = Project(axisX, axisZ, sfx, sfz, srx, srz, halfX, halfZ)
                + Project(axisX, axisZ, ofx, ofz, orx, orz, halfX, halfZ);
            float dist = dx * axisX + dz * axisZ;
            float penetration = span - Mathf.Abs(dist);
            if (penetration <= 0f)
                return false;
            if (penetration < best)
            {
                float sign = dist >= 0f ? -1f : 1f;
                if (Mathf.Abs(dist) < 0.0001f)
                    sign = -1f;
                best = penetration;
                pushX = axisX * sign * penetration;
                pushZ = axisZ * sign * penetration;
            }

            return true;
        }

        static bool Slab(float origin, float direction, float min, float max, ref float tMin, ref float tMax)
        {
            if (Mathf.Abs(direction) < 1e-6f)
                return origin >= min && origin <= max;

            float inv = 1f / direction;
            float t1 = (min - origin) * inv;
            float t2 = (max - origin) * inv;
            if (t1 > t2)
            {
                float swap = t1;
                t1 = t2;
                t2 = swap;
            }

            if (t1 > tMin)
                tMin = t1;
            if (t2 < tMax)
                tMax = t2;
            return tMin <= tMax;
        }
    }

    public enum VoxelRole : byte
    {
        None,
        Armor,
        Wheel,
        Engine,
        Gun,
        Turret,
        Glass
    }

    public struct PartStats
    {
        public float mass;
        public float power;
        public float drive;
        public bool canSteer;
        public bool canFire;
        public bool canAim;
    }

    public struct BallisticHit
    {
        public bool car;
        public bool surface;
        public int block;
        public int target;
        public Vector3 point;
        public float distance;
    }

    /// <summary>
    /// Прочность каждой клетки от 0 до 15. Ноль значит, что кубик уже отвалился.
    /// Длина зависит от модели и по сети едет целиком.
    /// </summary>
    public struct VoxelMask : INetworkSerializable, IEquatable<VoxelMask>
    {
        public byte[] packed;

        public static VoxelMask Allocate(int cellCount)
        {
            int bytes = cellCount < 1 ? 0 : (cellCount + 1) >> 1;
            return new VoxelMask { packed = new byte[bytes] };
        }

        public bool IsEmpty
        {
            get
            {
                if (packed == null)
                    return true;
                for (int i = 0; i < packed.Length; i++)
                {
                    if (packed[i] != 0)
                        return false;
                }

                return true;
            }
        }

        public int Get(int index)
        {
            if (packed == null || index < 0)
                return 0;
            int slot = index >> 1;
            if (slot >= packed.Length)
                return 0;
            int shift = (index & 1) * 4;
            return (packed[slot] >> shift) & 15;
        }

        public bool IsSet(int index)
        {
            return Get(index) > 0;
        }

        public void Write(int index, int hp)
        {
            if (packed == null || index < 0)
                return;
            int slot = index >> 1;
            if (slot >= packed.Length)
                return;
            int shift = (index & 1) * 4;
            packed[slot] = (byte)((packed[slot] & ~(15 << shift)) | ((hp & 15) << shift));
        }

        public VoxelMask Set(int index, int hp)
        {
            var copy = new VoxelMask();
            if (packed == null)
                return copy;
            copy.packed = new byte[packed.Length];
            Buffer.BlockCopy(packed, 0, copy.packed, 0, packed.Length);
            copy.Write(index, hp);
            return copy;
        }

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            var data = packed;
            if (serializer.IsWriter && data == null)
                data = Array.Empty<byte>();
            serializer.SerializeValue(ref data);
            if (serializer.IsReader)
                packed = data == null || data.Length == 0 ? null : data;
        }

        public bool Equals(VoxelMask other)
        {
            int count = packed == null ? 0 : packed.Length;
            int otherCount = other.packed == null ? 0 : other.packed.Length;
            int length = count > otherCount ? count : otherCount;
            for (int i = 0; i < length; i++)
            {
                byte left = i < count ? packed[i] : (byte)0;
                byte right = i < otherCount ? other.packed[i] : (byte)0;
                if (left != right)
                    return false;
            }

            return true;
        }

        public override bool Equals(object obj)
        {
            return obj is VoxelMask other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                if (packed == null)
                    return hash;
                for (int i = 0; i < packed.Length; i++)
                    hash = hash * 31 + packed[i];
                return hash;
            }
        }
    }
}
