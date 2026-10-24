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

        readonly List<DriveInput> pending = new List<DriveInput>(64);
        readonly List<Outgoing> outbox = new List<Outgoing>(32);

        CarState predicted;
        CarState incoming;
        float incomingDue;
        bool incomingReady;
        bool placed;
        int nextTick = 1;
        int pendingReliableFire = -1;
        bool fireLatch;
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

            if ((Input.GetKeyDown(KeyCode.Space) || Input.GetMouseButtonDown(0)) && !PointerOverHud())
                fireLatch = true;

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
                turret = (Input.GetKey(KeyCode.E) ? 1 : 0) - (Input.GetKey(KeyCode.Q) ? 1 : 0),
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
                    SubmitFireRpc(input.tick);
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
                    SubmitFireRpc(item.input.tick);
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
        void SubmitFireRpc(int tick)
        {
            var state = ServerState.Value;
            if (tick <= state.tick)
            {
                if (state.lastShotTick == tick || state.cooldown > 0f || CarSim.Count(state.blocks) == 0)
                    return;
                state.cooldown = CarSim.ShotCooldown;
                state.lastShotTick = tick;
                ServerState.Value = state;
                ResolveShot(state);
                return;
            }

            pendingReliableFire = tick;
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
                pendingReliableFire = -1;
            }

            int shotBefore = state.lastShotTick;
            var next = CarSim.Apply(state, input, true);
            next = AvoidOthers(next);
            if (input.fire && next.lastShotTick != shotBefore)
                ResolveShot(next);
            ServerState.Value = next;
        }

        void ResolveShot(CarState shooter)
        {
            var cars = FindObjectsByType<CarAvatar>();
            for (int i = 0; i < cars.Length; i++)
            {
                var other = cars[i];
                if (other == this || !other.IsSpawned)
                    continue;
                var target = other.ServerState.Value;
                if (!CarSim.TryHit(shooter, target, out int block))
                    continue;
                target.blocks = CarSim.Damage(target.blocks, block);
                other.ServerState.Value = target;
                break;
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

        static bool PointerOverHud()
        {
            var pointer = Input.mousePosition;
            pointer.y = Screen.height - pointer.y;
            return pointer.x >= 12f && pointer.x <= 400f && pointer.y >= 12f && pointer.y <= 560f;
        }
    }
}
