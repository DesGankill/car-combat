using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace CarCombat
{
    /// <summary>
    /// Живая репликация декора арены тем же правилом, что и замер ReplicationBudget.
    /// </summary>
    public class ReplicationWorld : MonoBehaviour
    {
        const string Channel = "CarCombatRep";

        ReplicationBudget.Prop[] props = System.Array.Empty<ReplicationBudget.Prop>();
        readonly Dictionary<int, Transform> visuals = new Dictionary<int, Transform>();
        readonly int[] ids = new int[ReplicationBudget.PropCount];
        float accumulator;
        int tick;
        bool serverReady;
        bool registered;
        bool hooked;

        void Start()
        {
            Hook();
        }

        void Hook()
        {
            if (hooked)
                return;
            var manager = NetworkManager.Singleton;
            if (manager == null)
                return;
            manager.OnServerStarted += Bind;
            manager.OnClientStarted += Bind;
            manager.OnClientConnectedCallback += OnConnected;
            hooked = true;
            if (manager.IsListening)
                Bind();
        }

        void Bind()
        {
            var manager = NetworkManager.Singleton;
            if (manager == null || manager.CustomMessagingManager == null || registered)
                return;
            manager.CustomMessagingManager.RegisterNamedMessageHandler(Channel, OnPacket);
            registered = true;
        }

        void OnDisable()
        {
            var manager = NetworkManager.Singleton;
            if (manager == null)
                return;
            if (hooked)
            {
                manager.OnServerStarted -= Bind;
                manager.OnClientStarted -= Bind;
                manager.OnClientConnectedCallback -= OnConnected;
                hooked = false;
            }

            if (registered && manager.CustomMessagingManager != null)
                manager.CustomMessagingManager.UnregisterNamedMessageHandler(Channel);
            registered = false;
        }

        void Update()
        {
            Hook();
            var manager = NetworkManager.Singleton;
            if (manager == null || !manager.IsServer)
                return;
            if (!serverReady)
            {
                EnsureProps();
                serverReady = true;
            }

            accumulator += Time.deltaTime;
            while (accumulator >= 1f / ReplicationBudget.TickHz)
            {
                accumulator -= 1f / ReplicationBudget.TickHz;
                tick++;
                ApplyPoses(tick);
                SendDeltas(tick);
            }
        }

        void OnConnected(ulong clientId)
        {
            var manager = NetworkManager.Singleton;
            if (manager == null || !manager.IsServer)
                return;
            EnsureProps();
            serverReady = true;
            ApplyPoses(tick);
            if (clientId == NetworkManager.ServerClientId)
                return;
            Send(clientId, tick, true);
        }

        void EnsureProps()
        {
            if (props.Length == 0)
                props = ReplicationBudget.Build();
            for (int i = 0; i < props.Length; i++)
                EnsureVisual(i, props[i].kind);
        }

        void ApplyPoses(int frame)
        {
            for (int i = 0; i < props.Length; i++)
            {
                if (!visuals.TryGetValue(i, out var body) || body == null)
                    continue;
                ReplicationBudget.Pose(props[i], frame, out float x, out float z, out _, out _, out _);
                if (props[i].kind == ReplicationBudget.Kind.Pickup && ((i == 40 && frame >= 20) || (i == 41 && frame >= 60)))
                {
                    body.gameObject.SetActive(false);
                    continue;
                }

                body.gameObject.SetActive(true);
                var scale = body.localScale.x;
                body.position = new Vector3(x, scale * 0.5f, z);
            }
        }

        void SendDeltas(int frame)
        {
            var manager = NetworkManager.Singleton;
            if (manager == null)
                return;
            foreach (var clientId in manager.ConnectedClientsIds)
            {
                if (clientId == NetworkManager.ServerClientId)
                    continue;
                Send(clientId, frame, false);
            }
        }

        void Send(ulong clientId, int frame, bool forceAll)
        {
            Observer(clientId, out float x, out float z, out float yaw);
            int count = forceAll
                ? ReplicationBudget.PropCount
                : ReplicationBudget.Collect(props, frame, x, z, yaw, true, ids);
            if (!forceAll && count == 0)
                return;
            int bytes = ReplicationBudget.HeaderBytes + count * ReplicationBudget.QuantRecordBytes;
            var writer = new FastBufferWriter(bytes + 32, Allocator.Temp);
            writer.WriteValueSafe((byte)1);
            writer.WriteValueSafe((ushort)count);
            writer.WriteValueSafe((uint)frame);
            int limit = forceAll ? props.Length : count;
            for (int n = 0; n < limit; n++)
            {
                int id = forceAll ? n : ids[n];
                ReplicationBudget.Pose(props[id], frame, out float px, out float pz, out float pyaw, out float vx, out float vz);
                writer.WriteValueSafe((ushort)id);
                writer.WriteValueSafe((byte)props[id].kind);
                writer.WriteValueSafe((short)Mathf.Clamp(Mathf.RoundToInt(px * 100f), short.MinValue, short.MaxValue));
                writer.WriteValueSafe((short)Mathf.Clamp(Mathf.RoundToInt(pz * 100f), short.MinValue, short.MaxValue));
                writer.WriteValueSafe((byte)Mathf.RoundToInt(Mathf.Repeat(pyaw, 360f) / 360f * 255f));
                writer.WriteValueSafe((sbyte)Mathf.Clamp(Mathf.RoundToInt(vx * 10f), -127, 127));
                writer.WriteValueSafe((sbyte)Mathf.Clamp(Mathf.RoundToInt(vz * 10f), -127, 127));
            }

            var messaging = NetworkManager.Singleton != null ? NetworkManager.Singleton.CustomMessagingManager : null;
            if (messaging != null)
                messaging.SendNamedMessage(Channel, clientId, writer, NetworkDelivery.UnreliableSequenced);
            writer.Dispose();
        }

        void OnPacket(ulong sender, FastBufferReader reader)
        {
            var manager = NetworkManager.Singleton;
            if (manager != null && manager.IsServer)
                return;
            reader.ReadValueSafe(out byte _);
            reader.ReadValueSafe(out ushort count);
            reader.ReadValueSafe(out uint frame);
            if (props.Length == 0)
                props = ReplicationBudget.Build();
            for (int i = 0; i < count; i++)
            {
                reader.ReadValueSafe(out ushort id);
                reader.ReadValueSafe(out byte kind);
                reader.ReadValueSafe(out short xCm);
                reader.ReadValueSafe(out short zCm);
                reader.ReadValueSafe(out byte _);
                reader.ReadValueSafe(out sbyte _);
                reader.ReadValueSafe(out sbyte _);
                var body = EnsureVisual(id, (ReplicationBudget.Kind)kind);
                bool taken = kind == (byte)ReplicationBudget.Kind.Pickup
                    && ((id == 40 && frame >= 20) || (id == 41 && frame >= 60));
                if (taken)
                {
                    body.gameObject.SetActive(false);
                    continue;
                }

                body.gameObject.SetActive(true);
                float scale = body.localScale.x;
                body.position = new Vector3(xCm / 100f, scale * 0.5f, zCm / 100f);
            }
        }

        Transform EnsureVisual(int id, ReplicationBudget.Kind kind)
        {
            if (visuals.TryGetValue(id, out var existing) && existing != null)
                return existing;
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var collider = cube.GetComponent<Collider>();
            collider.enabled = false;
            Destroy(collider);
            cube.name = kind + "_" + id;
            float scale = kind == ReplicationBudget.Kind.Cover ? 0.9f : kind == ReplicationBudget.Kind.Pickup ? 0.35f : 0.5f;
            cube.transform.localScale = Vector3.one * scale;
            var renderer = cube.GetComponent<Renderer>();
            renderer.sharedMaterial = Shared();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var property = new MaterialPropertyBlock();
            Color color = kind == ReplicationBudget.Kind.Cover
                ? new Color(0.32f, 0.34f, 0.36f)
                : kind == ReplicationBudget.Kind.Pickup
                    ? new Color(0.25f, 0.72f, 0.38f)
                    : new Color(0.75f, 0.28f, 0.18f);
            property.SetColor("_Color", color);
            renderer.SetPropertyBlock(property);
            visuals[id] = cube.transform;
            return cube.transform;
        }

        static void Observer(ulong clientId, out float x, out float z, out float yaw)
        {
            x = 0f;
            z = 0f;
            yaw = 0f;
            var cars = FindObjectsByType<CarAvatar>();
            for (int i = 0; i < cars.Length; i++)
            {
                if (!cars[i].IsSpawned || cars[i].OwnerClientId != clientId)
                    continue;
                var state = cars[i].ServerState.Value;
                x = state.x;
                z = state.z;
                yaw = state.yaw;
                return;
            }
        }

        static Material shared;

        static Material Shared()
        {
            if (shared != null)
                return shared;
            var shader = Shader.Find("Standard");
            shared = new Material(shader != null ? shader : Shader.Find("Diffuse"));
            shared.color = Color.white;
            return shared;
        }
    }
}
