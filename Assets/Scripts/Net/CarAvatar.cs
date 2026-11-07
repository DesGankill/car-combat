using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace CarCombat
{
    /// <summary>
    /// Сетевая машина.
    /// Владелец (autonomous) шлёт ввод и при включённом предсказании сразу двигает свою копию.
    /// Сервер прогоняет тот же CarSim.Apply и рассылает позу.
    /// Чужие машины (simulated) только рисуют серверную позу.
    /// </summary>
    public class CarAvatar : NetworkBehaviour
    {
        public NetworkVariable<CarState> ServerState = new NetworkVariable<CarState>(
            default,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        public CarState RenderState { get; private set; }
        public bool HasState { get; private set; }
        public bool PredictionEnabled { get; private set; } = true;
        public int PendingCount => pending.Count;
        public string Notice { get; private set; } = "";
        public int SelectedShell { get; private set; }

        readonly List<DriveInput> pending = new List<DriveInput>(64);
        readonly List<Outgoing> outbox = new List<Outgoing>(32);

        CarState predicted;
        CarState incoming;
        float incomingDue;
        bool incomingReady;
        bool placed;
        int nextTick = 1;
        int pendingReliableFire = -1;
        int pendingShell;
        bool fireLatch;
        bool aimReady;
        bool scoped;
        int aimYaw;
        int aimPitch;
        float noticeUntil;
        CarView view;

        struct Outgoing
        {
            public float due;
            public DriveInput input;
            public bool reliableFire;
        }

        public override void OnNetworkSpawn()
        {
            view = GetComponent<CarView>();
            ServerState.OnValueChanged += OnServerState;
            if (IsServer)
                ServerState.Value = CarSim.Spawn((int)OwnerClientId);

            ApplySnapshot(ServerState.Value);
            view.Paint(OwnerClientId);
        }

        public override void OnNetworkDespawn()
        {
            ServerState.OnValueChanged -= OnServerState;
        }

        void Update()
        {
            if (!IsSpawned || !IsOwner)
                return;

            if (Input.GetKeyDown(KeyCode.F1))
                SetPrediction(!PredictionEnabled);

            if (Input.GetKeyDown(KeyCode.F2))
                ShiftLocally();

            if (Input.GetKeyDown(KeyCode.F3))
            {
                predicted.cooldown = 0f;
                RenderState = predicted;
                fireLatch = true;
            }

            for (int i = 0; i < Solid.Count && i < 9; i++)
            {
                if (Input.GetKeyDown(KeyCode.Alpha1 + i) || Input.GetKeyDown(KeyCode.Keypad1 + i))
                    SelectedShell = i;
            }

            if ((Input.GetKeyDown(KeyCode.Space) || Input.GetMouseButtonDown(0)) && !MatchHud.ContainsPointer())
                fireLatch = true;

            if (HasState)
            {
                bool scope = AimScope.Open;
                if (scope && !scoped)
                {
                    float world = Mathf.Repeat(RenderState.yaw + RenderState.turret + 180f, 360f) - 180f;
                    aimYaw = Mathf.RoundToInt(world);
                    aimPitch = Mathf.RoundToInt(Mathf.Clamp(RenderState.pitch, CarSim.PitchMin, CarSim.PitchMax));
                }

                if (scope)
                    ReadScopeAim();
                else
                    aimYaw = ReadWorldAim(RenderState);
                if (!scope && scoped)
                {
                    Cursor.lockState = CursorLockMode.None;
                    Cursor.visible = true;
                }

                scoped = scope;
                aimReady = true;
            }

            FlushOutbox();
            FlushSnapshot();
            if (Time.time > noticeUntil)
                Notice = "";
        }

        void FixedUpdate()
        {
            if (!IsSpawned || !IsOwner || !HasState)
                return;

            var input = new DriveInput
            {
                tick = nextTick++,
                throttle = ReadAxis(KeyCode.W, KeyCode.UpArrow, KeyCode.S, KeyCode.DownArrow),
                steer = ReadAxis(KeyCode.D, KeyCode.RightArrow, KeyCode.A, KeyCode.LeftArrow),
                turret = aimReady ? aimYaw : Mathf.RoundToInt(predicted.yaw + predicted.turret),
                pitch = aimReady ? aimPitch : Mathf.RoundToInt(predicted.pitch),
                shell = SelectedShell,
                snapAim = AimScope.Open,
                fire = fireLatch
            };
            fireLatch = false;

            pending.Add(input);
            if (PredictionEnabled)
            {
                predicted = CarSim.Apply(predicted, input, true);
                predicted = AvoidOthers(predicted);
                predicted.blocks = ServerBlocks();
                RenderState = predicted;
                if (input.fire && predicted.lastShotTick == input.tick)
                    view.ShowTracer(predicted);
            }

            Send(input);
        }

        void LateUpdate()
        {
            if (!HasState)
                return;

            var targetPos = new Vector3(RenderState.x, 0f, RenderState.z);
            var targetRot = Quaternion.Euler(0f, RenderState.yaw, 0f);
            if (!placed)
            {
                transform.SetPositionAndRotation(targetPos, targetRot);
                placed = true;
                return;
            }

            if (!IsOwner)
            {
                float t = 1f - Mathf.Exp(-14f * Time.deltaTime);
                transform.position = Vector3.Lerp(transform.position, targetPos, t);
                transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, t);
                return;
            }

            float error = Vector2.Distance(
                new Vector2(transform.position.x, transform.position.z),
                new Vector2(targetPos.x, targetPos.z));
            if (!PredictionEnabled || error > 1.2f)
                transform.SetPositionAndRotation(targetPos, targetRot);
            else
            {
                float t = 1f - Mathf.Exp(-22f * Time.deltaTime);
                transform.position = Vector3.Lerp(transform.position, targetPos, t);
                transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, t);
            }
        }

        public void SetPrediction(bool enabled)
        {
            PredictionEnabled = enabled;
            if (!HasState)
                return;
            if (!enabled)
            {
                predicted = LastServerOrRender();
                RenderState = predicted;
            }
            else
            {
                predicted = RenderState;
            }
        }

        void ShiftLocally()
        {
            if (!HasState)
                return;
            predicted.x += 8f;
            RenderState = predicted;
            ShowNotice("Локальный сдвиг. Сервер его не получал.");
        }

        void Send(DriveInput input)
        {
            if (IsServer)
            {
                ApplyServerInput(input);
                return;
            }

            float lag = Mathf.Max(0, NetEmulation.OneWayLagMs) / 1000f;
            if (!NetEmulation.ShouldDrop())
                Enqueue(input, lag, false);
            if (input.fire && !NetEmulation.ShouldDrop())
                Enqueue(input, lag, true);
        }

        void Enqueue(DriveInput input, float lag, bool reliableFire)
        {
            if (lag <= 0f)
            {
                if (reliableFire)
                    SubmitFireRpc(input.tick, input.shell);
                else
                    SubmitDriveRpc(input);
                return;
            }

            outbox.Add(new Outgoing
            {
                due = Time.time + lag,
                input = input,
                reliableFire = reliableFire
            });
        }

        void FlushOutbox()
        {
            for (int i = 0; i < outbox.Count;)
            {
                if (Time.time < outbox[i].due)
                {
                    i++;
                    continue;
                }

                var item = outbox[i];
                outbox.RemoveAt(i);
                if (item.reliableFire)
                    SubmitFireRpc(item.input.tick, item.input.shell);
                else
                    SubmitDriveRpc(item.input);
            }
        }

        void FlushSnapshot()
        {
            if (!incomingReady || Time.time < incomingDue)
                return;
            incomingReady = false;
            ApplySnapshot(incoming);
        }

        [Rpc(SendTo.Server, Delivery = RpcDelivery.Unreliable, InvokePermission = RpcInvokePermission.Owner)]
        void SubmitDriveRpc(DriveInput input)
        {
            ApplyServerInput(input);
        }

        [Rpc(SendTo.Server, Delivery = RpcDelivery.Reliable, InvokePermission = RpcInvokePermission.Owner)]
        void SubmitFireRpc(int tick, int shell)
        {
            var state = ServerState.Value;
            if (tick <= state.tick)
            {
                if (state.lastShotTick == tick || state.cooldown > 0f || CarSim.Count(state.blocks) == 0)
                    return;
                state.cooldown = CarSim.ShotCooldown;
                state.lastShotTick = tick;
                state.lastShell = shell;
                ServerState.Value = state;
                ResolveShot(state, shell);
                return;
            }

            pendingReliableFire = tick;
            pendingShell = shell;
        }

        void ApplyServerInput(DriveInput input)
        {
            if (!IsServer)
                return;

            var state = ServerState.Value;
            if (input.tick <= state.tick)
                return;

            if (pendingReliableFire > 0 && input.tick >= pendingReliableFire)
            {
                input.fire = true;
                input.shell = pendingShell;
                pendingReliableFire = -1;
            }

            int shotBefore = state.lastShotTick;
            var next = CarSim.Apply(state, input, true);
            next = AvoidOthers(next);
            if (input.fire && next.lastShotTick != shotBefore)
                ResolveShot(next, input.shell);
            ServerState.Value = next;
        }

        void ResolveShot(CarState shooter, int shellIndex)
        {
            var cars = FindObjectsByType<CarAvatar>();
            var states = new CarState[4];
            var avatars = new CarAvatar[4];
            int count = 0;
            for (int i = 0; i < cars.Length && count < states.Length; i++)
            {
                if (cars[i] == this || !cars[i].IsSpawned)
                    continue;
                avatars[count] = cars[i];
                states[count] = cars[i].ServerState.Value;
                count++;
            }

            var hit = CarSim.Fly(shooter, states, count, null, 0, out _);
            var shell = Solid.Of(shellIndex);
            for (int i = 0; i < count; i++)
            {
                int direct = hit.car && hit.target == i ? hit.block : -1;
                if (direct < 0 && shell.splashRadius <= 0f)
                    continue;
                var target = states[i];
                var damaged = CarSim.ApplyShell(target.blocks, target, hit.point, shellIndex, direct);
                if (damaged.Equals(target.blocks))
                    continue;
                target.blocks = damaged;
                avatars[i].ServerState.Value = target;
            }
        }

        void OnServerState(CarState previous, CarState next)
        {
            if (!IsOwner)
            {
                RenderState = next;
                HasState = true;
                return;
            }

            if (IsServer)
            {
                ApplySnapshot(next);
                return;
            }

            if (NetEmulation.ShouldDrop())
                return;

            if (NetEmulation.OneWayLagMs <= 0)
            {
                ApplySnapshot(next);
                return;
            }

            if (!incomingReady)
                incomingDue = Time.time + NetEmulation.OneWayLagMs / 1000f;
            incoming = next;
            incomingReady = true;
        }

        void ApplySnapshot(CarState next)
        {
            int previousShot = HasState ? RenderState.lastShotTick : 0;
            NoteRejectedShots(next);
            pending.RemoveAll(command => command.tick <= next.tick);

            if (!PredictionEnabled)
            {
                predicted = next;
                RenderState = next;
                HasState = true;
                if (next.lastShotTick != previousShot && next.lastShotTick != 0 && view != null)
                    view.ShowTracer(next);
                return;
            }

            var replay = next;
            for (int i = 0; i < pending.Count; i++)
                replay = CarSim.Apply(replay, pending[i], true);
            replay = AvoidOthers(replay);

            replay.blocks = next.blocks;
            replay.lastShotTick = next.lastShotTick;
            replay.lastShell = next.lastShell;
            predicted = replay;
            RenderState = replay;
            HasState = true;
        }

        void NoteRejectedShots(CarState next)
        {
            for (int i = 0; i < pending.Count; i++)
            {
                var command = pending[i];
                if (!command.fire || command.tick > next.tick)
                    continue;
                if (next.lastShotTick >= command.tick)
                    continue;
                if (!CarSim.Evaluate(next.blocks).canFire)
                    ShowNotice("Пушка уничтожена.");
                else if (next.cooldown > 0.05f)
                    ShowNotice("Сервер отклонил выстрел: кулдаун ещё не прошёл.");
                else
                    ShowNotice("Выстрел не дошёл до сервера.");
                return;
            }
        }

        CarState AvoidOthers(CarState state)
        {
            var cars = FindObjectsByType<CarAvatar>();
            for (int i = 0; i < cars.Length; i++)
            {
                if (cars[i] == this || !cars[i].IsSpawned)
                    continue;
                state = CarSim.Collide(state, cars[i].ServerState.Value);
            }

            return state;
        }

        VoxelMask ServerBlocks()
        {
            return HasState ? ServerState.Value.blocks : predicted.blocks;
        }

        CarState LastServerOrRender()
        {
            return ServerState.Value.tick > 0 || !ServerState.Value.blocks.IsEmpty
                ? ServerState.Value
                : RenderState;
        }

        void ShowNotice(string text)
        {
            Notice = text;
            noticeUntil = Time.time + 2.5f;
        }

        static int ReadAxis(KeyCode positive, KeyCode positiveAlt, KeyCode negative, KeyCode negativeAlt)
        {
            int value = 0;
            if (Input.GetKey(positive) || Input.GetKey(positiveAlt))
                value += 1;
            if (Input.GetKey(negative) || Input.GetKey(negativeAlt))
                value -= 1;
            return value;
        }

        void ReadScopeAim()
        {
            const float look = 3.2f;
            float yaw = aimYaw + Input.GetAxis("Mouse X") * look;
            float pitch = aimPitch + Input.GetAxis("Mouse Y") * look;
            aimYaw = Mathf.RoundToInt(Mathf.Repeat(yaw + 180f, 360f) - 180f);
            aimPitch = Mathf.RoundToInt(Mathf.Clamp(pitch, CarSim.PitchMin, CarSim.PitchMax));
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        int ReadWorldAim(CarState state)
        {
            var camera = Camera.main;
            if (camera == null)
                return aimYaw;
            var ray = camera.ScreenPointToRay(Input.mousePosition);
            float best = float.PositiveInfinity;
            var aimPoint = Vector3.zero;
            bool found = false;
            var cars = FindObjectsByType<CarAvatar>();
            for (int i = 0; i < cars.Length; i++)
            {
                if (cars[i] == this)
                    continue;
                var view = cars[i].GetComponent<CarView>();
                if (view == null || !view.RaycastSolid(ray, out Vector3 voxel, out float voxelDistance) || voxelDistance >= best)
                    continue;
                best = voxelDistance;
                aimPoint = voxel;
                found = true;
            }

            var ground = new Plane(Vector3.up, Vector3.zero);
            if (ground.Raycast(ray, out float groundDistance) && groundDistance > 0f && groundDistance < best)
            {
                aimPoint = ray.GetPoint(groundDistance);
                found = true;
            }

            if (!found)
                return aimYaw;

            CarSim.SolveAim(state, aimPoint, out aimYaw, out aimPitch);
            return aimYaw;
        }
    }
}
