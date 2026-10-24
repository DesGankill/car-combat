using UnityEngine;

namespace CarCombat
{
    public class CameraFollow : MonoBehaviour
    {
        const float Pitch = 32f;
        const float Yaw = 45f;

        Camera cam;
        float distance = 36f;

        void OnEnable()
        {
            cam = GetComponent<Camera>();
            float span = Mathf.Max(CarSim.HalfW, CarSim.HalfL) * 2f;
            float size = Mathf.Max(8.5f, span * 1.35f);
            distance = Mathf.Max(36f, size * 4.2f);
            if (cam != null)
            {
                cam.orthographic = true;
                cam.orthographicSize = size;
            }

            FitArena();
            Apply(Vector3.zero);
        }

        void LateUpdate()
        {
            var look = Vector3.zero;
            var cars = FindObjectsByType<CarAvatar>();
            for (int i = 0; i < cars.Length; i++)
            {
                if (!cars[i].IsSpawned || !cars[i].IsOwner)
                    continue;
                look = cars[i].transform.position;
                break;
            }

            Apply(look);
        }

        void Apply(Vector3 target)
        {
            var look = target + Vector3.up * 0.7f;
            var rotation = Quaternion.Euler(Pitch, Yaw, 0f);
            transform.SetPositionAndRotation(look - rotation * Vector3.forward * distance, rotation);
        }

        static void FitArena()
        {
            float bound = CarSim.ArenaBound;
            float span = bound * 2f + 4f;
            Fit("Floor", new Vector3(0f, -0.2f, 0f), new Vector3(span, 0.4f, span));
            float wall = bound + 0.4f;
            Fit("WallNorth", new Vector3(0f, 0.7f, wall), new Vector3(span, 1.4f, 0.6f));
            Fit("WallSouth", new Vector3(0f, 0.7f, -wall), new Vector3(span, 1.4f, 0.6f));
            Fit("WallEast", new Vector3(wall, 0.7f, 0f), new Vector3(0.6f, 1.4f, span));
            Fit("WallWest", new Vector3(-wall, 0.7f, 0f), new Vector3(0.6f, 1.4f, span));
        }

        static void Fit(string name, Vector3 position, Vector3 scale)
        {
            var box = GameObject.Find(name);
            if (box == null)
                return;
            box.transform.position = position;
            box.transform.localScale = scale;
        }
    }
}
