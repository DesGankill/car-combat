using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;

namespace CarCombat
{
    public class MatchHud : MonoBehaviour
    {
        string address = "127.0.0.1";
        string lagText = "0";
        string lossText = "0";
        string status = "Не подключено";

        void Awake()
        {
            Application.runInBackground = true;
        }

        void Start()
        {
            var manager = NetworkManager.Singleton;
            if (manager == null)
                return;
            manager.OnClientConnectedCallback += id => status = "В игре, клиент " + id;
            manager.OnClientDisconnectCallback += id =>
            {
                if (manager.IsServer || id == manager.LocalClientId)
                    status = "Отключено";
            };
        }

        void OnGUI()
        {
            var previous = GUI.skin.label.fontSize;
            GUI.skin.label.fontSize = 15;
            GUI.skin.button.fontSize = 15;
            GUI.skin.textField.fontSize = 15;
            GUILayout.BeginArea(new Rect(12f, 12f, 388f, 540f), GUI.skin.box);
            GUILayout.Label("Car Combat");

            var manager = NetworkManager.Singleton;
            if (manager == null)
            {
                GUILayout.Label("В сцене нет NetworkManager.");
                GUILayout.EndArea();
                GUI.skin.label.fontSize = previous;
                return;
            }

            if (!manager.IsClient && !manager.IsServer)
                DrawOffline(manager);
            else
                DrawOnline(manager);

            GUILayout.EndArea();
            GUI.skin.label.fontSize = previous;
        }

        void DrawOffline(NetworkManager manager)
        {
            GUILayout.Label(status);
            if (GUILayout.Button("Хост — listen server", GUILayout.Height(32f)))
            {
                EnsurePayload(manager);
                manager.StartHost();
                status = "Хост запущен";
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label("Адрес", GUILayout.Width(52f));
            address = GUILayout.TextField(address, GUILayout.Width(140f));
            GUILayout.EndHorizontal();
            if (GUILayout.Button("Клиент", GUILayout.Height(32f)))
            {
                var transport = manager.GetComponent<UnityTransport>();
                EnsurePayload(manager);
                transport.SetConnectionData(address, 7777);
                if (manager.StartClient())
                    status = "Подключение к " + address;
                else
                    status = "Клиент не запустился";
            }

            GUILayout.Label("Сначала хост в этом окне, затем собранный exe как клиент.");
        }

        static void EnsurePayload(NetworkManager manager)
        {
            var transport = manager.GetComponent<UnityTransport>();
            if (transport == null)
                return;
            int bytes = 256 + (CarSim.BlockCount + 1) / 2;
            if (bytes > transport.MaxPayloadSize)
                transport.MaxPayloadSize = bytes;
        }

        void DrawOnline(NetworkManager manager)
        {
            string role = manager.IsServer ? "Роль: хост (listen server)" : "Роль: клиент";
            GUILayout.Label(role + "  |  " + status);
            var local = LocalCar();
            if (local == null)
            {
                GUILayout.Label("Ожидание машины...");
                DrawEmulation();
                if (GUILayout.Button("Отключиться"))
                    manager.Shutdown();
                return;
            }

            string prediction = local.PredictionEnabled ? "ВКЛ" : "ВЫКЛ";
            GUILayout.Label("Предсказание: " + prediction + "   (F1)");
            var state = local.RenderState;
            GUILayout.Label(
                "Скорость " + state.speed.ToString("0.0")
                + "   блоки " + CarSim.Count(state.blocks) + "/" + CarSim.BlueprintCount
                + "   башня " + state.turret.ToString("0") + "°");
            var parts = CarSim.Evaluate(state.blocks);
            GUILayout.Label(
                "Ход " + (parts.drive * 100f).ToString("0") + "%"
                + "   мощность " + parts.power.ToString("0")
                + "   масса " + parts.mass.ToString("0.0"));
            GUILayout.Label("Неподтверждённых команд: " + local.PendingCount);
            if (!string.IsNullOrEmpty(local.Notice))
                GUILayout.Label(local.Notice);

            GUILayout.Label("WASD — газ и руль. Q/E — башня. Пробел или ЛКМ — выстрел.");
            GUILayout.Label("F2 — локально увести машину. F3 — выстрел сквозь кулдаун.");
            if (GUILayout.Button(local.PredictionEnabled ? "Выключить предсказание" : "Включить предсказание"))
                local.SetPrediction(!local.PredictionEnabled);

            DrawEmulation();
            if (!manager.IsServer)
                GUILayout.Label("Задержка и потери действуют на этом клиенте.");
            else
                GUILayout.Label("Хост локальный: эмуляция видна на окне клиента.");

            if (GUILayout.Button("Отключиться"))
                manager.Shutdown();
        }

        void DrawEmulation()
        {
            GUILayout.Space(6f);
            GUILayout.Label("Задержка в одну сторону, мс");
            lagText = GUILayout.TextField(lagText);
            GUILayout.Label("Потери пакетов, %");
            lossText = GUILayout.TextField(lossText);
            if (GUILayout.Button("Применить эмуляцию"))
                ApplyEmulation();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("0 мс"))
                ApplyPreset(0, 0);
            if (GUILayout.Button("100 мс"))
                ApplyPreset(100, 0);
            if (GUILayout.Button("180 мс + 4%"))
                ApplyPreset(180, 4);
            GUILayout.EndHorizontal();
        }

        void ApplyPreset(int lag, int loss)
        {
            lagText = lag.ToString();
            lossText = loss.ToString();
            ApplyEmulation();
        }

        void ApplyEmulation()
        {
            int lag = NetEmulation.OneWayLagMs;
            int loss = NetEmulation.LossPercent;
            if (!int.TryParse(lagText, out lag))
                lag = NetEmulation.OneWayLagMs;
            if (!int.TryParse(lossText, out loss))
                loss = NetEmulation.LossPercent;
            NetEmulation.Reset(Mathf.Clamp(lag, 0, 1000), Mathf.Clamp(loss, 0, 100));
            lagText = NetEmulation.OneWayLagMs.ToString();
            lossText = NetEmulation.LossPercent.ToString();
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
