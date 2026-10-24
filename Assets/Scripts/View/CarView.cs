using System;
using UnityEngine;

namespace CarCombat
{
    public class CarView : MonoBehaviour
    {
        static readonly Color[] Palette =
        {
            new Color(0.90f, 0.28f, 0.22f),
            new Color(0.20f, 0.55f, 0.95f),
            new Color(0.95f, 0.75f, 0.18f),
            new Color(0.25f, 0.75f, 0.45f)
        };

        static Material shared;

        struct Cube
        {
            public int index;
            public Transform transform;
            public Renderer renderer;
            public Color color;
        }

        Transform turretPivot;
        Cube[] cubes = Array.Empty<Cube>();
        VoxelMask shownMask;
        bool maskReady;
        CarAvatar avatar;
        int seenShot = int.MinValue;

        void Awake()
        {
            avatar = GetComponent<CarAvatar>();
            BuildBlocks();
        }

        void LateUpdate()
        {
            if (avatar == null || !avatar.HasState)
                return;

            ApplyMask(avatar.RenderState.blocks);
            if (turretPivot != null)
                turretPivot.localRotation = Quaternion.Euler(0f, avatar.RenderState.turret, 0f);
            if (!avatar.IsOwner && avatar.RenderState.lastShotTick != seenShot && seenShot != int.MinValue)
                ShowTracer(avatar.RenderState);
            seenShot = avatar.RenderState.lastShotTick;
        }

        public void Paint(ulong clientId)
        {
            var body = Palette[clientId % (ulong)Palette.Length];
            for (int i = 0; i < cubes.Length; i++)
            {
                int index = cubes[i].index;
                VoxelRole role = CarSim.RoleOf(index);
                Color color;
                if (role == VoxelRole.Wheel)
                    color = new Color(0.07f, 0.07f, 0.08f);
                else if (role == VoxelRole.Glass)
                    color = Color.Lerp(body, new Color(0.75f, 0.9f, 1f), 0.72f);
                else if (role == VoxelRole.Engine)
                    color = new Color(0.45f, 0.24f, 0.12f);
                else if (role == VoxelRole.Gun)
                    color = new Color(0.16f, 0.16f, 0.17f);
                else if (role == VoxelRole.Turret)
                    color = new Color(0.28f, 0.29f, 0.32f);
                else if (CarSim.MaxHp(index) >= 5)
                    color = Color.Lerp(body, Color.black, 0.42f);
                else
                    color = body;
                cubes[i].color = color;
            }

            maskReady = false;
        }

        public void ShowTracer(CarState state)
        {
            CarSim.Muzzle(state, out Vector3 origin, out Vector3 forward);
            var tracer = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(tracer.GetComponent<Collider>());
            const float length = 11f;
            tracer.transform.position = origin + forward * (length * 0.5f);
            tracer.transform.rotation = Quaternion.LookRotation(forward);
            tracer.transform.localScale = new Vector3(0.06f, 0.06f, length);
            var renderer = tracer.GetComponent<Renderer>();
            renderer.sharedMaterial = SharedMaterial();
            var property = new MaterialPropertyBlock();
            property.SetColor("_Color", new Color(1f, 0.85f, 0.35f));
            renderer.SetPropertyBlock(property);
            Destroy(tracer, 0.12f);
        }

        void BuildBlocks()
        {
            turretPivot = new GameObject("Turret").transform;
            turretPivot.SetParent(transform, false);
            turretPivot.localPosition = CarSim.TurretPivot;
            var material = SharedMaterial();
            float scale = CarSim.Cell * 0.998f;
            int filled = CarSim.BlueprintCount;
            cubes = new Cube[filled];
            int written = 0;
            for (int i = 0; i < CarSim.BlockCount && written < filled; i++)
            {
                if (!CarSim.Blueprint.IsSet(i))
                    continue;
                CarSim.CellOf(i, out int ix, out int iy, out int iz);
                CarSim.CellCenter(i, out float x, out float y, out float z);
                var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Destroy(cube.GetComponent<Collider>());
                cube.name = "Block_" + ix + "_" + iy + "_" + iz;
                VoxelRole role = CarSim.RoleOf(i);
                bool mounted = role == VoxelRole.Gun || role == VoxelRole.Turret;
                var parent = mounted ? turretPivot : transform;
                cube.transform.SetParent(parent, false);
                var center = new Vector3(x, y, z);
                cube.transform.localPosition = mounted ? center - CarSim.TurretPivot : center;
                cube.transform.localScale = new Vector3(scale, scale, scale);
                var renderer = cube.GetComponent<Renderer>();
                renderer.sharedMaterial = material;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                cubes[written++] = new Cube { index = i, transform = cube.transform, renderer = renderer };
            }
        }

        void ApplyMask(VoxelMask mask)
        {
            if (maskReady && mask.Equals(shownMask))
                return;
            shownMask = mask;
            maskReady = true;
            var property = new MaterialPropertyBlock();
            for (int i = 0; i < cubes.Length; i++)
            {
                var cube = cubes[i];
                if (cube.transform == null)
                    continue;
                int hp = mask.Get(cube.index);
                cube.transform.gameObject.SetActive(hp > 0);
                if (hp <= 0)
                    continue;
                float shade = hp / (float)Mathf.Max(1, CarSim.MaxHp(cube.index));
                property.SetColor("_Color", Color.Lerp(cube.color * 0.35f, cube.color, shade));
                cube.renderer.SetPropertyBlock(property);
            }
        }

        static Material SharedMaterial()
        {
            if (shared != null)
                return shared;
            var shader = Shader.Find("Standard");
            shared = new Material(shader != null ? shader : Shader.Find("Diffuse"));
            shared.color = Color.white;
            if (shared.HasProperty("_Glossiness"))
                shared.SetFloat("_Glossiness", 0.12f);
            return shared;
        }
    }
}
