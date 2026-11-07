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
        Transform gunPivot;
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
            if (gunPivot != null)
                gunPivot.localRotation = Quaternion.Euler(-avatar.RenderState.pitch, 0f, 0f);
            if (avatar.IsOwner && GetComponent<ShotPreview>() == null)
                gameObject.AddComponent<ShotPreview>();
            if (!avatar.IsOwner && avatar.RenderState.lastShotTick != seenShot && seenShot != int.MinValue)
                ShowTracer(avatar.RenderState);
            seenShot = avatar.RenderState.lastShotTick;
        }

        public const int ScopeHiddenLayer = 30;

        public void SetHiddenFromScope(bool hidden)
        {
            int layer = hidden ? ScopeHiddenLayer : 0;
            for (int i = 0; i < cubes.Length; i++)
            {
                if (cubes[i].transform != null)
                    cubes[i].transform.gameObject.layer = layer;
            }
        }

        public bool RaycastSolid(Ray ray, out Vector3 point, out float distance)
        {
            point = default;
            distance = float.PositiveInfinity;
            var direction = ray.direction.sqrMagnitude > 0.0001f ? ray.direction.normalized : Vector3.forward;
            bool found = false;
            for (int i = 0; i < cubes.Length; i++)
            {
                var cube = cubes[i];
                if (cube.renderer == null || !cube.transform.gameObject.activeInHierarchy)
                    continue;
                if (!cube.renderer.bounds.IntersectRay(new Ray(ray.origin, direction), out float hit) || hit < 0f || hit >= distance)
                    continue;
                distance = hit;
                point = ray.origin + direction * hit;
                found = true;
            }

            return found;
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
            var targets = new CarState[4];
            int countCars = 0;
            var cars = FindObjectsByType<CarAvatar>();
            for (int i = 0; i < cars.Length && countCars < targets.Length; i++)
            {
                if (cars[i] == avatar || !cars[i].HasState)
                    continue;
                targets[countCars++] = cars[i].RenderState;
            }

            var samples = new Vector3[160];
            CarSim.Fly(state, targets, countCars, samples, samples.Length, out int count);
            if (count <= 0)
                return;
            var voxel = GameObject.CreatePrimitive(PrimitiveType.Cube);
            voxel.name = "Shell";
            voxel.AddComponent<ShellVoxel>().Launch(samples, count, Solid.Of(state.lastShell), CarSim.BulletStep);
        }

        void BuildBlocks()
        {
            turretPivot = new GameObject("Turret").transform;
            turretPivot.SetParent(transform, false);
            turretPivot.localPosition = CarSim.TurretPivot;
            gunPivot = new GameObject("Gun").transform;
            gunPivot.SetParent(turretPivot, false);
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
                var parent = role == VoxelRole.Gun ? gunPivot : mounted ? turretPivot : transform;
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
