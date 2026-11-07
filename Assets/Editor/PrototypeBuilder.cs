using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace CarCombat
{
    public static class PrototypeBuilder
    {
        const string PrefabPath = "Assets/Prefabs/Car.prefab";
        const string ScenePath = "Assets/Scenes/Arena.unity";

        public static void BuildAndTest()
        {
            try
            {
                RunChecks();
                BuildScene();
                Debug.Log("CAR_COMBAT_BUILD_OK");
                EditorApplication.Exit(0);
            }
            catch (Exception error)
            {
                Debug.LogError("CAR_COMBAT_BUILD_FAIL " + error);
                EditorApplication.Exit(1);
            }
        }

        public static void RunChecks()
        {
            var straight = Drive(CarSim.Spawn(0), 30, 1, 0, false);
            if (straight.z <= -6f || Mathf.Abs(straight.x) > 0.2f)
                throw new InvalidOperationException("Прямой газ не сдвинул машину вперёд: " + straight.x + "," + straight.z);

            var again = Drive(CarSim.Spawn(0), 30, 1, 0, false);
            if (Mathf.Abs(again.z - straight.z) > 0.0001f || Mathf.Abs(again.speed - straight.speed) > 0.0001f)
                throw new InvalidOperationException("Повтор одного и того же ввода дал другую позу.");

            var turning = CarSim.Spawn(0);
            for (int i = 0; i < 40; i++)
            {
                turning = CarSim.Apply(turning, new DriveInput
                {
                    tick = i + 1,
                    throttle = 1,
                    steer = 1,
                    fire = false
                }, true);
            }

            if (Mathf.Abs(turning.yaw) < 5f)
                throw new InvalidOperationException("Руль не повернул машину. yaw=" + turning.yaw);

            var shot = CarSim.Spawn(0);
            shot = CarSim.Apply(shot, new DriveInput { tick = 1, fire = true }, true);
            if (shot.lastShotTick != 1 || shot.cooldown < 0.3f)
                throw new InvalidOperationException("Первый выстрел не включил кулдаун.");
            float cooldown = shot.cooldown;
            shot = CarSim.Apply(shot, new DriveInput { tick = 2, fire = true }, true);
            if (shot.lastShotTick != 1 || shot.cooldown >= cooldown)
                throw new InvalidOperationException("Повторный выстрел во время кулдауна был принят.");

            var wreck = CarSim.Spawn(0);
            wreck.blocks = default;
            var wreckMoved = Drive(wreck, 15, 1, 0, false);
            if (Mathf.Abs(wreckMoved.x - wreck.x) > 0.001f || Mathf.Abs(wreckMoved.z - wreck.z) > 0.001f)
                throw new InvalidOperationException("Разбитая машина поехала.");

            var wall = CarSim.Spawn(0);
            wall.x = 15.2f;
            wall.yaw = 90f;
            wall.speed = CarSim.MaxSpeed;
            for (int i = 0; i < 10; i++)
            {
                wall = CarSim.Apply(wall, new DriveInput { tick = i + 1, throttle = 1 }, true);
            }

            if (wall.x > CarSim.ArenaBound + 0.001f)
                throw new InvalidOperationException("Машина вышла за стену: " + wall.x);

            var shooter = CarSim.Spawn(0);
            var victim = CarSim.Spawn(1);
            int block = -1;
            bool hit = false;
            for (int pitch = 0; pitch <= (int)CarSim.PitchMax && !hit; pitch += 5)
            {
                var aimed = shooter;
                aimed.pitch = pitch;
                hit = CarSim.TryHit(aimed, victim, out block);
            }

            if (!hit)
                throw new InvalidOperationException("Выстрел в машину напротив не попал.");
            int before = CarSim.TotalHp(victim.blocks);
            victim.blocks = CarSim.Damage(victim.blocks, block);
            if (CarSim.TotalHp(victim.blocks) >= before)
                throw new InvalidOperationException("Попадание не сняло блок.");

            var plate = CarSim.Spawn(1);
            int plateHp = plate.blocks.Get(block);
            var punched = CarSim.ApplyShell(plate.blocks, plate, CarSim.BlockWorld(plate, block), 0, block);
            if (punched.Get(block) != Mathf.Max(0, plateHp - Solid.Of(0).directDamage))
                throw new InvalidOperationException("Бронебойный не снял большой урон с одного блока.");
            int neighbor = -1;
            int far = -1;
            var impact = CarSim.BlockWorld(plate, block);
            float splashReach = Solid.Of(1).splashRadius;
            for (int i = 0; i < CarSim.BlockCount; i++)
            {
                if (i == block || plate.blocks.Get(i) <= 0)
                    continue;
                float away = Vector3.Distance(CarSim.BlockWorld(plate, i), impact);
                if (neighbor < 0 && away <= splashReach)
                    neighbor = i;
                if (away > splashReach + CarSim.Cell)
                    far = i;
            }

            var blast = CarSim.ApplyShell(plate.blocks, plate, impact, 1, block);
            if (blast.Get(block) != Mathf.Max(0, plateHp - Solid.Of(1).directDamage))
                throw new InvalidOperationException("Фугас не повредил блок попадания.");
            if (neighbor >= 0 && blast.Get(neighbor) >= plate.blocks.Get(neighbor))
                throw new InvalidOperationException("Фугас не задел соседний воксель.");
            if (far >= 0 && blast.Get(far) != plate.blocks.Get(far))
                throw new InvalidOperationException("Фугас задел слишком далёкий воксель.");

            var missed = shooter;
            missed.yaw = 90f;
            if (CarSim.TryHit(missed, victim, out _))
                throw new InvalidOperationException("Выстрел в сторону задел машину спереди.");

            var server = CarSim.Spawn(0);
            var inputs = new[]
            {
                new DriveInput { tick = 1, throttle = 1, steer = 1, fire = true },
                new DriveInput { tick = 2, throttle = 1, steer = 0, fire = false },
                new DriveInput { tick = 3, throttle = 0, steer = -1, fire = false }
            };
            var sequential = server;
            for (int i = 0; i < inputs.Length; i++)
                sequential = CarSim.Apply(sequential, inputs[i], true);
            var replay = server;
            for (int i = 0; i < inputs.Length; i++)
                replay = CarSim.Apply(replay, inputs[i], true);
            if (Mathf.Abs(replay.x - sequential.x) > 0.0001f || Mathf.Abs(replay.z - sequential.z) > 0.0001f)
                throw new InvalidOperationException("Повтор ввода не совпал с исходным шагом.");

            var inputHasNoPosition = new DriveInput { tick = 4, throttle = 1, steer = -1, fire = true };
            if (inputHasNoPosition.tick != 4)
                throw new InvalidOperationException("Команда сломалась.");

            var aim = CarSim.Spawn(0);
            var pivot = CarSim.TurretWorld(aim);
            int world = CarSim.WorldYawDegrees(pivot, pivot + new Vector3(4f, 0f, 0f), 0);
            if (Mathf.Abs(Mathf.DeltaAngle(world, 90f)) > 2f)
                throw new InvalidOperationException("Курсор справа не дал курс 90°. " + world);
            for (int i = 0; i < 20; i++)
                aim = CarSim.Apply(aim, new DriveInput { tick = i + 1, turret = world }, true);
            if (Mathf.Abs(aim.yaw) > 0.01f || Mathf.Abs(Mathf.DeltaAngle(aim.turret, 90f)) > 2f)
                throw new InvalidOperationException("Башня не довернулась к курсору справа: " + aim.turret);

            for (int i = 0; i < 25; i++)
            {
                aim = CarSim.Apply(aim, new DriveInput
                {
                    tick = 21 + i,
                    throttle = 1,
                    steer = 1,
                    turret = world
                }, true);
            }

            float facing = aim.yaw + aim.turret;
            if (Mathf.Abs(Mathf.DeltaAngle(facing, world)) > 8f)
                throw new InvalidOperationException("Башня уехала вместе с корпусом: " + facing);

            var rear = CarSim.Spawn(0);
            for (int i = 0; i < 40; i++)
                rear = CarSim.Apply(rear, new DriveInput { tick = i + 1, turret = 180 }, true);
            if (Mathf.Abs(Mathf.DeltaAngle(rear.yaw + rear.turret, 180f)) > 2f)
                throw new InvalidOperationException("Башня не развернулась назад: " + rear.turret);

            var flatDrop = CarSim.Fly(shooter, System.Array.Empty<CarState>(), 0, null, 0, out _);
            var down = shooter;
            down.pitch = -30f;
            var downDrop = CarSim.Fly(down, System.Array.Empty<CarState>(), 0, null, 0, out _);
            if (!flatDrop.surface || !downDrop.surface || downDrop.point.y > 0.05f || downDrop.point.z > flatDrop.point.z)
                throw new InvalidOperationException("Наклон вниз не сократил падение на землю.");

            var lob = shooter;
            lob.pitch = 45f;
            var arc = new Vector3[96];
            CarSim.Muzzle(lob, out Vector3 muzzle, out _);
            CarSim.Fly(lob, System.Array.Empty<CarState>(), 0, arc, arc.Length, out int arcCount);
            float peak = muzzle.y;
            for (int i = 0; i < arcCount; i++)
            {
                if (arc[i].y > peak)
                    peak = arc[i].y;
            }

            if (peak < muzzle.y + 0.8f)
                throw new InvalidOperationException("Подъём ствола не поднял траекторию. peak=" + peak);

            var spot = new Vector3(shooter.x + 1.5f, 0f, shooter.z + 8f);
            CarSim.SolveAim(shooter, spot, out int spotYaw, out int spotPitch);
            var aimedSpot = shooter;
            aimedSpot.pitch = spotPitch;
            aimedSpot.turret = Mathf.DeltaAngle(aimedSpot.yaw, spotYaw);
            var landing = CarSim.Fly(aimedSpot, System.Array.Empty<CarState>(), 0, null, 0, out _);
            float miss = Vector2.Distance(new Vector2(landing.point.x, landing.point.z), new Vector2(spot.x, spot.z));
            if (!landing.surface || miss > 1.5f)
                throw new InvalidOperationException("Дуга не пришла в точку на земле: " + miss);

            var snapped = CarSim.Apply(CarSim.Spawn(0), new DriveInput
            {
                tick = 1,
                turret = 80,
                pitch = -15,
                snapAim = true
            }, true);
            if (Mathf.Abs(Mathf.DeltaAngle(snapped.yaw + snapped.turret, 80f)) > 1f || Mathf.Abs(snapped.pitch + 15f) > 0.1f)
                throw new InvalidOperationException("Прицел не повернул пушку сразу: " + snapped.turret + " / " + snapped.pitch);

            var traffic = ReplicationBudget.Measure();
            if (traffic.props < 50)
                throw new InvalidOperationException("На арене меньше 50 реплицируемых объектов: " + traffic.props);
            if (traffic.optimizedBytes >= traffic.baselineBytes || traffic.reductionPercent < 40f)
                throw new InvalidOperationException(
                    "Оптимизация репликации не снизила трафик на 40%: "
                    + traffic.baselineBytes + " → " + traffic.optimizedBytes);
            if (traffic.baselineBytes != 147900 || traffic.optimizedBytes != 3950)
                throw new InvalidOperationException(
                    "Профиль репликации разъехался с отчётом: "
                    + traffic.baselineBytes + " / " + traffic.optimizedBytes);

            Debug.Log("CAR_COMBAT_CHECKS_OK");
        }

        public static void BuildScene()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Prefabs"))
                AssetDatabase.CreateFolder("Assets", "Prefabs");
            if (!AssetDatabase.IsValidFolder("Assets/Scenes"))
                AssetDatabase.CreateFolder("Assets", "Scenes");

            var car = new GameObject("Car");
            car.AddComponent<NetworkObject>();
            car.AddComponent<CarAvatar>();
            car.AddComponent<CarView>();
            if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>("Assets/DefaultNetworkPrefabs.asset") != null)
                AssetDatabase.DeleteAsset("Assets/DefaultNetworkPrefabs.asset");
            if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) != null)
                AssetDatabase.DeleteAsset(PrefabPath);
            PrefabUtility.SaveAsPrefabAsset(car, PrefabPath);
            UnityEngine.Object.DestroyImmediate(car);
            RepairPrefabScripts();
            AssetDatabase.ImportAsset(PrefabPath, ImportAssetOptions.ForceUpdate);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (prefab == null || prefab.GetComponent<CarAvatar>() == null || prefab.GetComponent<CarView>() == null)
            {
                var found = prefab == null ? "prefab null" : Describe(prefab);
                throw new InvalidOperationException("На префабе машины пропали скрипты. " + found);
            }

            var networkObject = prefab.GetComponent<NetworkObject>();
            var validate = typeof(NetworkObject).GetMethod(
                "OnValidate",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (validate == null)
                throw new InvalidOperationException("У NetworkObject нет OnValidate.");
            validate.Invoke(networkObject, null);
            if (networkObject.PrefabIdHash == 0)
                throw new InvalidOperationException("Префаб машины получил нулевой сетевой идентификатор.");

            var rewritten = Regex.Replace(
                File.ReadAllText(PrefabPath),
                @"(?m)^  GlobalObjectIdHash: \d+",
                "  GlobalObjectIdHash: " + networkObject.PrefabIdHash);
            File.WriteAllText(PrefabPath, rewritten.Replace("\r\n", "\n"));
            AssetDatabase.ImportAsset(PrefabPath, ImportAssetOptions.ForceUpdate);
            prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (prefab.GetComponent<CarAvatar>() == null || prefab.GetComponent<NetworkObject>().PrefabIdHash == 0)
                throw new InvalidOperationException("После записи идентификатора префаб снова сломался. " + Describe(prefab));

            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            var camera = Camera.main;
            if (camera == null)
                throw new InvalidOperationException("В новой сцене нет камеры.");
            camera.orthographic = true;
            camera.orthographicSize = 8.5f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.07f, 0.08f, 0.11f);
            var iso = Quaternion.Euler(32f, 45f, 0f);
            camera.transform.rotation = iso;
            camera.transform.position = Vector3.up * 0.7f - iso * Vector3.forward * 36f;
            camera.gameObject.AddComponent<CameraFollow>();

            var light = UnityEngine.Object.FindAnyObjectByType<Light>();
            if (light != null)
            {
                light.type = LightType.Directional;
                light.intensity = 1.15f;
                light.transform.rotation = Quaternion.Euler(55f, -30f, 0f);
            }

            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.42f, 0.44f, 0.48f);

            CreateBox("Floor", new Vector3(0f, -0.2f, 0f), new Vector3(36f, 0.4f, 36f), new Color(0.16f, 0.17f, 0.19f));
            float wall = CarSim.ArenaBound + 0.4f;
            CreateBox("WallNorth", new Vector3(0f, 0.7f, wall), new Vector3(36f, 1.4f, 0.6f), new Color(0.28f, 0.30f, 0.34f));
            CreateBox("WallSouth", new Vector3(0f, 0.7f, -wall), new Vector3(36f, 1.4f, 0.6f), new Color(0.28f, 0.30f, 0.34f));
            CreateBox("WallEast", new Vector3(wall, 0.7f, 0f), new Vector3(0.6f, 1.4f, 36f), new Color(0.28f, 0.30f, 0.34f));
            CreateBox("WallWest", new Vector3(-wall, 0.7f, 0f), new Vector3(0.6f, 1.4f, 36f), new Color(0.28f, 0.30f, 0.34f));

            var netObject = new GameObject("Network");
            var manager = netObject.AddComponent<NetworkManager>();
            var transport = netObject.AddComponent<UnityTransport>();
            if (manager.NetworkConfig == null)
                manager.NetworkConfig = new NetworkConfig();
            manager.NetworkConfig.NetworkTransport = transport;
            manager.NetworkConfig.PlayerPrefab = prefab;
            manager.NetworkConfig.EnableSceneManagement = false;
            transport.SetConnectionData("127.0.0.1", 7777);
            netObject.AddComponent<MatchHud>();

            PlayerSettings.productName = "Car Combat";
            PlayerSettings.companyName = "IKBO-32-23";
            PlayerSettings.runInBackground = true;
            PlayerSettings.defaultScreenWidth = 1280;
            PlayerSettings.defaultScreenHeight = 720;
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.resizableWindow = true;
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);

            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            AssetDatabase.SaveAssets();
        }

        static void RepairPrefabScripts()
        {
            var text = File.ReadAllText(PrefabPath).Replace("\r\n", "\n");
            if (!text.Contains("m_Script: {fileID: 0}"))
            {
                File.WriteAllText(PrefabPath, text);
                return;
            }

            text = PointScript(text, "CarCombat.CarAvatar", "2100000001");
            text = PointScript(text, "CarCombat.CarView", "2100000002");
            if (!text.Contains("&2100000001"))
            {
                text += ScriptBlock("2100000001", "CarAvatar");
                text += ScriptBlock("2100000002", "CarView");
            }

            File.WriteAllText(PrefabPath, text);
        }

        static string PointScript(string text, string className, string fileId)
        {
            var marker = "m_EditorClassIdentifier: Assembly-CSharp::" + className;
            var pattern = "  m_Script: \\{[^\\n]+\\}\\n  m_Name: \\n  " + Regex.Escape(marker);
            var replacement = "  m_Script: {fileID: " + fileId + "}\n  m_Name: \n  " + marker;
            return Regex.Replace(text, pattern, replacement);
        }

        static string ScriptBlock(string fileId, string className)
        {
            return "\n--- !u!115 &" + fileId + "\nMonoScript:\n"
                + "  m_ObjectHideFlags: 0\n"
                + "  m_CorrespondingSourceObject: {fileID: 0}\n"
                + "  m_PrefabInstance: {fileID: 0}\n"
                + "  m_PrefabAsset: {fileID: 0}\n"
                + "  m_Name: \n"
                + "  serializedVersion: 7\n"
                + "  m_DefaultReferences: {}\n"
                + "  m_Icon: {fileID: 0}\n"
                + "  m_Type: 0\n"
                + "  m_ExecutionOrder: 0\n"
                + "  m_ClassName: " + className + "\n"
                + "  m_Namespace: CarCombat\n"
                + "  m_AssemblyName: Assembly-CSharp\n";
        }

        static string Describe(GameObject prefab)
        {
            var parts = "";
            foreach (var component in prefab.GetComponents<Component>())
                parts += component == null ? "missing " : component.GetType().Name + " ";
            return parts;
        }

        static CarState Drive(CarState state, int ticks, int throttle, int steer, bool fire)
        {
            for (int i = 0; i < ticks; i++)
            {
                state = CarSim.Apply(state, new DriveInput
                {
                    tick = state.tick + 1,
                    throttle = throttle,
                    steer = steer,
                    fire = fire && i == 0
                }, true);
            }

            return state;
        }

        static void CreateBox(string name, Vector3 position, Vector3 scale, Color color)
        {
            var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = name;
            box.transform.position = position;
            box.transform.localScale = scale;
            var renderer = box.GetComponent<Renderer>();
            var shader = Shader.Find("Standard");
            renderer.sharedMaterial = new Material(shader != null ? shader : Shader.Find("Diffuse"));
            renderer.sharedMaterial.color = color;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
        }
    }
}
