using UnityEngine;

namespace CarCombat
{
    /// <summary>
    /// Полупрозрачная дуга и воксель попадания. Только у того, кто целится.
    /// После выстрела метка гаснет и не остаётся в мире. Коллайдера нет.
    /// </summary>
    public class ShotPreview : MonoBehaviour
    {
        const int Pool = 96;

        static Material ghost;

        Transform[] marks = System.Array.Empty<Transform>();
        readonly CarState[] targets = new CarState[4];
        readonly Vector3[] samples = new Vector3[Pool];
        CarAvatar avatar;
        int seenShot;
        bool armed;
        bool hidden;
        float lockTurret;
        float lockPitch;

        void Awake()
        {
            avatar = GetComponent<CarAvatar>();
            marks = new Transform[Pool];
            var material = GhostMaterial();
            float scale = CarSim.Cell;
            for (int i = 0; i < Pool; i++)
            {
                var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                var collider = cube.GetComponent<Collider>();
                collider.enabled = false;
                Destroy(collider);
                cube.name = "ShotGhost";
                cube.transform.SetParent(null, false);
                cube.transform.localScale = new Vector3(scale, scale, scale);
                var renderer = cube.GetComponent<Renderer>();
                renderer.sharedMaterial = material;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                cube.SetActive(false);
                marks[i] = cube.transform;
            }
        }

        void OnDestroy()
        {
            for (int i = 0; i < marks.Length; i++)
            {
                if (marks[i] != null)
                    Destroy(marks[i].gameObject);
            }
        }

        void LateUpdate()
        {
            if (avatar == null || !avatar.IsOwner || !avatar.HasState || !CarSim.Evaluate(avatar.RenderState.blocks).canFire)
            {
                Hide();
                return;
            }

            var state = avatar.RenderState;
            if (!armed)
            {
                seenShot = state.lastShotTick;
                armed = true;
            }
            else if (state.lastShotTick != seenShot)
            {
                seenShot = state.lastShotTick;
                hidden = true;
                lockTurret = state.turret;
                lockPitch = state.pitch;
                Hide();
                return;
            }

            if (hidden)
            {
                if (Mathf.Abs(Mathf.DeltaAngle(lockTurret, state.turret)) < 2f && Mathf.Abs(state.pitch - lockPitch) < 2f)
                {
                    Hide();
                    return;
                }

                hidden = false;
            }

            Draw(state);
        }

        void Draw(CarState state)
        {
            int countCars = 0;
            var cars = FindObjectsByType<CarAvatar>();
            for (int i = 0; i < cars.Length && countCars < targets.Length; i++)
            {
                if (cars[i] == avatar || !cars[i].HasState)
                    continue;
                targets[countCars++] = cars[i].RenderState;
            }

            var hit = CarSim.Fly(state, targets, countCars, samples, samples.Length, out int count);
            int drawn = 0;
            if (!AimScope.Open)
            {
                Vector3 last = new Vector3(float.NaN, 0f, 0f);
                for (int i = 0; i < count && drawn < Pool - 1; i++)
                {
                    if ((hit.car || hit.surface) && (samples[i] - hit.point).sqrMagnitude < 0.01f)
                        continue;
                    if (!float.IsNaN(last.x) && (samples[i] - last).sqrMagnitude < CarSim.Cell * CarSim.Cell)
                        continue;
                    Place(drawn++, samples[i]);
                    last = samples[i];
                }
            }

            if (hit.car || hit.surface)
            {
                var point = hit.point;
                if (hit.surface)
                    point.y = Mathf.Max(point.y, CarSim.Cell * 0.5f);
                Place(drawn++, point);
            }

            for (int i = drawn; i < marks.Length; i++)
                marks[i].gameObject.SetActive(false);
        }

        void Place(int index, Vector3 point)
        {
            var mark = marks[index];
            mark.gameObject.SetActive(true);
            mark.SetParent(null, false);
            mark.position = point;
            mark.rotation = Quaternion.identity;
            mark.localScale = Vector3.one * CarSim.Cell;
        }

        void Hide()
        {
            for (int i = 0; i < marks.Length; i++)
            {
                if (marks[i] != null)
                    marks[i].gameObject.SetActive(false);
            }
        }

        public static Material GhostMaterial()
        {
            if (ghost != null)
                return ghost;
            var shader = Shader.Find("Standard");
            ghost = new Material(shader != null ? shader : Shader.Find("Diffuse"));
            if (ghost.HasProperty("_Mode"))
            {
                ghost.SetFloat("_Mode", 3f);
                ghost.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                ghost.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                ghost.SetInt("_ZWrite", 0);
                ghost.DisableKeyword("_ALPHATEST_ON");
                ghost.EnableKeyword("_ALPHABLEND_ON");
                ghost.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                ghost.renderQueue = 3000;
            }

            ghost.color = new Color(1f, 0.78f, 0.28f, 0.38f);
            return ghost;
        }
    }
}
