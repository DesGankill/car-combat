using UnityEngine;

namespace CarCombat
{
    /// <summary>
    /// Летящий снаряд размером с воксель. Коллайдера нет, после попадания исчезает.
    /// </summary>
    public class ShellVoxel : MonoBehaviour
    {
        Vector3[] path = System.Array.Empty<Vector3>();
        float elapsed;
        float duration = 0.02f;
        float splashRadius;
        bool burst;

        public void Launch(Vector3[] samples, int count, Solid.Shell shell, float step)
        {
            int n = Mathf.Clamp(count, 0, samples == null ? 0 : samples.Length);
            path = new Vector3[Mathf.Max(1, n)];
            if (n > 0)
                System.Array.Copy(samples, path, n);
            duration = Mathf.Max(step, n * step);
            splashRadius = shell.splashRadius;
            transform.position = path[0];
            transform.localScale = Vector3.one * CarSim.Cell;
            var collider = GetComponent<Collider>();
            if (collider != null)
            {
                collider.enabled = false;
                Destroy(collider);
            }

            var renderer = GetComponent<Renderer>();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.sharedMaterial = Opaque();
            var property = new MaterialPropertyBlock();
            property.SetColor("_Color", shell.color);
            renderer.SetPropertyBlock(property);
        }

        void Update()
        {
            if (path.Length == 0)
            {
                Destroy(gameObject);
                return;
            }

            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / duration);
            float f = t * (path.Length - 1);
            int i = Mathf.FloorToInt(f);
            int j = Mathf.Min(i + 1, path.Length - 1);
            transform.position = Vector3.Lerp(path[i], path[j], f - i);
            if (t < 1f)
                return;
            if (splashRadius > 0f)
                Burst(path[path.Length - 1]);
            Destroy(gameObject);
        }

        void Burst(Vector3 center)
        {
            if (burst)
                return;
            burst = true;
            Vector3[] offsets =
            {
                Vector3.right, Vector3.left, Vector3.forward, Vector3.back, Vector3.up
            };
            for (int i = 0; i < offsets.Length; i++)
            {
                var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                var collider = cube.GetComponent<Collider>();
                collider.enabled = false;
                Destroy(collider);
                cube.transform.position = center + offsets[i] * CarSim.Cell;
                cube.transform.localScale = Vector3.one * CarSim.Cell;
                var renderer = cube.GetComponent<Renderer>();
                renderer.sharedMaterial = ShotPreview.GhostMaterial();
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                Destroy(cube, 0.18f);
            }
        }

        static Material opaque;

        static Material Opaque()
        {
            if (opaque != null)
                return opaque;
            var shader = Shader.Find("Standard");
            opaque = new Material(shader != null ? shader : Shader.Find("Diffuse"));
            opaque.color = Color.white;
            return opaque;
        }
    }
}
