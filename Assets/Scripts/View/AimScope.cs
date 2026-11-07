using UnityEngine;

namespace CarCombat
{
    /// <summary>
    /// Окно прицела по ПКМ. Камера смотрит вдоль луча ствола, перекрестие — центр этого луча.
    /// </summary>
    public class AimScope : MonoBehaviour
    {
        const int MarkerLayer = 31;

        public static bool Open { get; private set; }

        Camera scope;
        RenderTexture texture;
        GameObject marker;
        string caption = "";
        bool hidOwner;
        static Texture2D pixel;

        void Awake()
        {
            texture = new RenderTexture(512, 512, 16, RenderTextureFormat.ARGB32);
            texture.Create();

            var cameraObject = new GameObject("AimScopeCamera");
            scope = cameraObject.AddComponent<Camera>();
            scope.enabled = false;
            scope.orthographic = true;
            scope.orthographicSize = 0.85f;
            scope.clearFlags = CameraClearFlags.SolidColor;
            scope.backgroundColor = new Color(0.05f, 0.06f, 0.08f);
            scope.nearClipPlane = 0.2f;
            scope.farClipPlane = 40f;
            scope.targetTexture = texture;
            scope.cullingMask = ~(1 << CarView.ScopeHiddenLayer);
            scope.depth = 50;

            marker = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(marker.GetComponent<Collider>());
            marker.name = "AimMarker";
            marker.layer = MarkerLayer;
            marker.transform.localScale = Vector3.one * CarSim.Cell * 1.08f;
            marker.SetActive(false);
            var renderer = marker.GetComponent<Renderer>();
            renderer.sharedMaterial = MarkerMaterial();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            var main = Camera.main;
            if (main != null)
                main.cullingMask &= ~(1 << MarkerLayer);
        }

        void OnDestroy()
        {
            if (texture != null)
                texture.Release();
            ShowOwner(true);
            if (scope != null)
                Destroy(scope.gameObject);
            if (marker != null)
                Destroy(marker);
        }

        void Update()
        {
            var local = LocalCar();
            bool held = Input.GetMouseButton(1);
            if (!held || local == null || !local.HasState)
                Open = false;
            else if (!Open && MatchHud.ContainsPointer())
                Open = false;
            else
                Open = true;
        }

        void LateUpdate()
        {
            if (scope == null)
                return;

            var main = Camera.main;
            if (main != null)
                main.cullingMask &= ~(1 << MarkerLayer);

            if (!Open)
            {
                scope.enabled = false;
                if (marker != null)
                    marker.SetActive(false);
                ShowOwner(true);
                return;
            }

            ShowOwner(false);

            var local = LocalCar();
            if (local == null || !local.HasState)
            {
                scope.enabled = false;
                marker.SetActive(false);
                return;
            }

            var state = local.RenderState;
            CarSim.Muzzle(state, out Vector3 origin, out Vector3 direction);
            if (direction.sqrMagnitude < 0.0001f)
                direction = Vector3.forward;
            scope.farClipPlane = CarSim.ShotRange + 4f;
            scope.transform.SetPositionAndRotation(origin, Quaternion.LookRotation(direction, Vector3.up));
            scope.enabled = true;
            if (!CarSim.Evaluate(state.blocks).canFire)
            {
                marker.SetActive(false);
                caption = "Пушка уничтожена.";
                return;
            }

            PlaceMarker(local, state);
        }

        void PlaceMarker(CarAvatar local, CarState state)
        {
            CarAvatar hitCar = null;
            int hitBlock = -1;
            float nearest = float.MaxValue;
            var cars = FindObjectsByType<CarAvatar>();
            for (int i = 0; i < cars.Length; i++)
            {
                var other = cars[i];
                if (other == local || !other.HasState)
                    continue;
                if (!CarSim.TryHit(state, other.RenderState, out int block, out float distance))
                    continue;
                if (distance >= nearest)
                    continue;
                nearest = distance;
                hitBlock = block;
                hitCar = other;
            }

            if (hitCar == null)
            {
                marker.SetActive(false);
                caption = "В створе пусто";
                return;
            }

            marker.SetActive(true);
            marker.transform.SetPositionAndRotation(
                CarSim.BlockWorld(hitCar.RenderState, hitBlock),
                Quaternion.Euler(0f, hitCar.RenderState.yaw, 0f));
            caption = "Попадание в подсвеченный блок";
        }

        void OnGUI()
        {
            if (!Open || texture == null)
                return;

            var window = new Rect(12f, 12f, 300f, 318f);
            var previous = GUI.color;
            GUI.color = new Color(0.02f, 0.02f, 0.03f);
            GUI.DrawTexture(window, Pixel());
            GUI.color = Color.white;
            GUI.DrawTexture(new Rect(window.x + 4f, window.y + 4f, 292f, 292f), texture, ScaleMode.ScaleToFit);
            DrawCrosshair(new Rect(window.x + 4f, window.y + 4f, 292f, 292f));
            GUI.Label(new Rect(window.x + 8f, window.y + 294f, 284f, 22f), caption);
            GUI.color = previous;
        }

        static void DrawCrosshair(Rect view)
        {
            float cx = view.x + view.width * 0.5f;
            float cy = view.y + view.height * 0.5f;
            var previous = GUI.color;
            GUI.color = new Color(1f, 0.92f, 0.35f);
            GUI.DrawTexture(new Rect(cx - 18f, cy - 1f, 12f, 2f), Pixel());
            GUI.DrawTexture(new Rect(cx + 6f, cy - 1f, 12f, 2f), Pixel());
            GUI.DrawTexture(new Rect(cx - 1f, cy - 18f, 2f, 12f), Pixel());
            GUI.DrawTexture(new Rect(cx - 1f, cy + 6f, 2f, 12f), Pixel());
            GUI.color = previous;
        }

        static Texture2D Pixel()
        {
            if (pixel != null)
                return pixel;
            pixel = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            pixel.SetPixel(0, 0, Color.white);
            pixel.Apply();
            return pixel;
        }

        static Material MarkerMaterial()
        {
            var shader = Shader.Find("Standard");
            var material = new Material(shader != null ? shader : Shader.Find("Diffuse"));
            if (material.HasProperty("_Mode"))
            {
                material.SetFloat("_Mode", 3f);
                material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                material.SetInt("_ZWrite", 0);
                material.DisableKeyword("_ALPHATEST_ON");
                material.EnableKeyword("_ALPHABLEND_ON");
                material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                material.renderQueue = 3000;
            }

            material.color = new Color(1f, 0.82f, 0.15f, 0.45f);
            return material;
        }

        void ShowOwner(bool visible)
        {
            bool hide = !visible;
            if (hidOwner == hide)
                return;
            var local = LocalCar();
            if (local == null)
                return;
            var view = local.GetComponent<CarView>();
            if (view == null)
                return;
            view.SetHiddenFromScope(hide);
            hidOwner = hide;
        }

        static CarAvatar LocalCar()
        {
            var cars = FindObjectsByType<CarAvatar>();
            for (int i = 0; i < cars.Length; i++)
            {
                if (cars[i].IsSpawned && cars[i].IsOwner)
                    return cars[i];
            }

            return null;
        }
    }
}
