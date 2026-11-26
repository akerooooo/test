using System.IO;
using MimicGame.Environment;
using MimicGame.Game;
using MimicGame.Items;
using MimicGame.Network;
using MimicGame.Player;
using MimicGame.UI;
using Unity.Netcode;
using Unity.Netcode.Components;
using Unity.Netcode.Transports.UTP;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace MimicGame.EditorTools
{
    /// <summary>
    /// Menü: MimicGame → Test Sahnesini Oluştur
    /// Oyuncu, araç (ganimet alanı), karanlık bir bina, kapı ve ganimetlerle
    /// bir test sahnesi kurar ve Assets/_Project/Scenes/TestScene.unity olarak kaydeder.
    /// </summary>
    public static class TestSceneBuilder
    {
        const string SceneDir = "Assets/_Project/Scenes";
        const string ScenePath = SceneDir + "/TestScene.unity";
        const string MaterialDir = "Assets/_Project/Materials";
        const string PrefabDir = "Assets/_Project/Prefabs";
        const string PlayerPrefabPath = PrefabDir + "/Player.prefab";
        const string NetworkPrefabsListPath = "Assets/DefaultNetworkPrefabs.asset";

        [MenuItem("MimicGame/Test Sahnesini Oluştur")]
        public static void Build()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            if (File.Exists(ScenePath) &&
                !EditorUtility.DisplayDialog("Test sahnesi", "TestScene zaten var. Üzerine yazılsın mı?", "Evet", "Hayır"))
                return;

            Directory.CreateDirectory(SceneDir);
            Directory.CreateDirectory(MaterialDir);
            Directory.CreateDirectory(PrefabDir);
            AssetDatabase.Refresh();

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            SetupLighting();
            BuildGround();
            BuildVan(new Vector3(0f, 0f, -8f));
            BuildFacility(new Vector3(0f, 0f, 20f));
            var playerPrefab = BuildPlayerPrefab();
            BuildNetwork(playerPrefab);
            BuildMenuCamera();

            EditorSceneManager.SaveScene(scene, ScenePath);
            AddToBuildSettings(ScenePath);
            Debug.Log("Test sahnesi oluşturuldu: " + ScenePath + " — Play'e basıp deneyebilirsin.");
        }

        // ---------- Işık ve atmosfer ----------

        static void SetupLighting()
        {
            var moon = new GameObject("Ay Işığı");
            var l = moon.AddComponent<Light>();
            l.type = LightType.Directional;
            l.intensity = 0.25f;
            l.color = new Color(0.55f, 0.65f, 1f);
            l.shadows = LightShadows.Soft;
            moon.transform.rotation = Quaternion.Euler(35f, -40f, 0f);

            RenderSettings.skybox = null;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.035f, 0.035f, 0.045f);
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogDensity = 0.035f;
            RenderSettings.fogColor = new Color(0.02f, 0.02f, 0.03f);
        }

        // ---------- Zemin ----------

        static void BuildGround()
        {
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Zemin";
            ground.transform.localScale = new Vector3(10f, 1f, 10f);
            ground.GetComponent<Renderer>().sharedMaterial = Mat("Zemin", new Color(0.18f, 0.2f, 0.16f));
            ground.isStatic = true;
        }

        // ---------- Araç (ganimet alanı) ----------

        static void BuildVan(Vector3 pos)
        {
            var root = new GameObject("Araç");
            root.transform.position = pos;
            var body = Mat("Araç", new Color(0.55f, 0.45f, 0.2f));
            var t = root.transform;

            Box("Taban", t, new Vector3(0f, 0.15f, 0f), new Vector3(3f, 0.3f, 5f), body);
            Box("Sol Duvar", t, new Vector3(-1.45f, 1.3f, 0f), new Vector3(0.1f, 2f, 5f), body);
            Box("Sağ Duvar", t, new Vector3(1.45f, 1.3f, 0f), new Vector3(0.1f, 2f, 5f), body);
            Box("Ön Duvar", t, new Vector3(0f, 1.3f, -2.45f), new Vector3(3f, 2f, 0.1f), body);
            Box("Tavan", t, new Vector3(0f, 2.35f, 0f), new Vector3(3f, 0.1f, 5f), body);
            Box("Kabin", t, new Vector3(0f, 1.1f, -3.4f), new Vector3(2.8f, 1.6f, 1.8f), Mat("Kabin", new Color(0.3f, 0.3f, 0.32f)));

            var lamp = new GameObject("Kasa Lambası");
            lamp.transform.SetParent(t, false);
            lamp.transform.localPosition = new Vector3(0f, 2.1f, 0f);
            var l = lamp.AddComponent<Light>();
            l.type = LightType.Point;
            l.range = 6f;
            l.intensity = 1.5f;
            l.color = new Color(1f, 0.85f, 0.6f);

            var zone = new GameObject("Ganimet Alanı");
            zone.transform.SetParent(t, false);
            zone.transform.localPosition = new Vector3(0f, 1.3f, 0f);
            var box = zone.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.size = new Vector3(2.8f, 2f, 4.8f);
            zone.AddComponent<LootZone>();
        }

        // ---------- Bina ----------

        static void BuildFacility(Vector3 c)
        {
            var root = new GameObject("Bina");
            root.transform.position = c;
            var t = root.transform;
            var wall = Mat("Duvar", new Color(0.32f, 0.31f, 0.3f));
            var floor = Mat("Bina Zemini", new Color(0.12f, 0.12f, 0.12f));

            const float h = 4f, half = 10f, th = 0.3f;

            Box("Bina Zemini", t, new Vector3(0f, 0.02f, 0f), new Vector3(2 * half, 0.04f, 2 * half), floor);
            Box("Tavan", t, new Vector3(0f, h + th * 0.5f, 0f), new Vector3(2 * half, th, 2 * half), wall);
            Box("Kuzey Duvarı", t, new Vector3(0f, h / 2, half), new Vector3(2 * half, h, th), wall);
            Box("Doğu Duvarı", t, new Vector3(half, h / 2, 0f), new Vector3(th, h, 2 * half), wall);
            Box("Batı Duvarı", t, new Vector3(-half, h / 2, 0f), new Vector3(th, h, 2 * half), wall);

            // Güney duvarı, ortasında 2 m'lik kapı boşluğuyla
            Box("Güney Duvarı Sol", t, new Vector3(-5.5f, h / 2, -half), new Vector3(9f, h, th), wall);
            Box("Güney Duvarı Sağ", t, new Vector3(5.5f, h / 2, -half), new Vector3(9f, h, th), wall);
            Box("Kapı Üstü", t, new Vector3(0f, 3.25f, -half), new Vector3(2f, 1.5f, th), wall);

            // İç duvarlar (koridorlar)
            Box("İç Duvar 1", t, new Vector3(-3f, h / 2, -3f), new Vector3(th, h, 14f), wall);
            Box("İç Duvar 2", t, new Vector3(3f, h / 2, 2f), new Vector3(th, h, 16f), wall);
            // 3. duvar ile 1. duvar arasında 1.5 m geçit bırakılır (sol alt odaya giriş)
            Box("İç Duvar 3", t, new Vector3(-7.25f, h / 2, 3f), new Vector3(5.5f, h, th), wall);

            // Kapı
            var hinge = new GameObject("Giriş Kapısı");
            hinge.transform.SetParent(t, false);
            hinge.transform.localPosition = new Vector3(-1f, 0f, -half);
            // Sahnede yerleşik NetworkObject (üstünde başka NetworkObject yok)
            hinge.AddComponent<NetworkObject>();
            hinge.AddComponent<Door>();
            var panel = Box("Kapı Paneli", hinge.transform, new Vector3(1f, 1.25f, 0f), new Vector3(1.95f, 2.5f, 0.08f),
                Mat("Kapı", new Color(0.25f, 0.15f, 0.1f)));
            panel.isStatic = false;

            // Titreyen lambalar
            Lamp(t, new Vector3(0f, 3.6f, 2f));
            Lamp(t, new Vector3(-6.5f, 3.6f, 7f));
            Lamp(t, new Vector3(6.5f, 3.6f, -5f));

            // Ganimetler
            var items = t;
            Loot("Eski Saat", 25, 1f, PrimitiveType.Cylinder, new Color(0.8f, 0.7f, 0.3f), new Vector3(0.3f, 0.05f, 0.3f), items, new Vector3(-6f, 0.5f, -6f));
            Loot("Bakır Boru", 15, 6f, PrimitiveType.Cylinder, new Color(0.75f, 0.4f, 0.2f), new Vector3(0.12f, 0.6f, 0.12f), items, new Vector3(-7f, 0.7f, 6f));
            Loot("Kırık Radyo", 40, 8f, PrimitiveType.Cube, new Color(0.3f, 0.25f, 0.2f), new Vector3(0.5f, 0.3f, 0.25f), items, new Vector3(-5f, 0.5f, 8f));
            Loot("Oyuncak Ayı", 12, 1f, PrimitiveType.Sphere, new Color(0.6f, 0.4f, 0.25f), new Vector3(0.35f, 0.35f, 0.35f), items, new Vector3(0f, 0.5f, -2f));
            Loot("Altın Kupa", 80, 5f, PrimitiveType.Cylinder, new Color(1f, 0.8f, 0.1f), new Vector3(0.3f, 0.25f, 0.3f), items, new Vector3(0f, 0.5f, 8f));
            Loot("Teneke Kutu", 5, 1f, PrimitiveType.Cylinder, new Color(0.6f, 0.6f, 0.6f), new Vector3(0.15f, 0.12f, 0.15f), items, new Vector3(1.5f, 0.5f, -8f));
            Loot("Fotoğraf Makinesi", 55, 2f, PrimitiveType.Cube, new Color(0.1f, 0.1f, 0.1f), new Vector3(0.3f, 0.2f, 0.15f), items, new Vector3(6f, 0.5f, -4f));
            Loot("Akü", 30, 15f, PrimitiveType.Cube, new Color(0.15f, 0.3f, 0.15f), new Vector3(0.45f, 0.35f, 0.3f), items, new Vector3(7f, 0.5f, 4f));
            Loot("Gümüş Tabak", 35, 2f, PrimitiveType.Cylinder, new Color(0.85f, 0.85f, 0.9f), new Vector3(0.4f, 0.02f, 0.4f), items, new Vector3(5f, 0.5f, 8f));
            Loot("Eski Telefon", 20, 1f, PrimitiveType.Cube, new Color(0.5f, 0.1f, 0.1f), new Vector3(0.12f, 0.06f, 0.25f), items, new Vector3(-1.5f, 0.5f, 9f));
        }

        static void Lamp(Transform parent, Vector3 localPos)
        {
            var go = new GameObject("Lamba");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            var l = go.AddComponent<Light>();
            l.type = LightType.Point;
            l.range = 7f;
            l.intensity = 1.2f;
            l.color = new Color(1f, 0.8f, 0.55f);
            l.shadows = LightShadows.Soft;
            go.AddComponent<LightFlicker>();
        }

        static void Loot(string name, int value, float weight, PrimitiveType shape, Color color, Vector3 scale,
            Transform parent, Vector3 localPos)
        {
            var go = GameObject.CreatePrimitive(shape);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = scale;
            go.GetComponent<Renderer>().sharedMaterial = Mat("Ganimet " + name, color);

            // Küre/kapsül collider yerine kutu (yuvarlanmasın)
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.AddComponent<BoxCollider>();

            // Eşya durumunu sunucu tutar; reparent yok
            var no = go.AddComponent<NetworkObject>();
            no.AutoObjectParentSync = false;

            var item = go.AddComponent<Item>();
            item.displayName = name;
            item.value = value;
            item.weight = weight;
        }

        // ---------- Ağ ----------

        static void BuildNetwork(GameObject playerPrefab)
        {
            var go = new GameObject("NetworkManager");
            var nm = go.AddComponent<NetworkManager>();
            var utp = go.AddComponent<UnityTransport>();
            if (nm.NetworkConfig == null) nm.NetworkConfig = new NetworkConfig();
            nm.NetworkConfig.NetworkTransport = utp;
            nm.NetworkConfig.PlayerPrefab = playerPrefab;

            // Oyuncu prefabı bir NetworkPrefabsList içinde olmalı
            var list = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(NetworkPrefabsListPath);
            if (list == null)
            {
                list = ScriptableObject.CreateInstance<NetworkPrefabsList>();
                AssetDatabase.CreateAsset(list, NetworkPrefabsListPath);
            }
            if (!list.Contains(playerPrefab))
                list.Add(new NetworkPrefab { Prefab = playerPrefab });
            EditorUtility.SetDirty(list);
            AssetDatabase.SaveAssets();
            if (!nm.NetworkConfig.Prefabs.NetworkPrefabsLists.Contains(list))
                nm.NetworkConfig.Prefabs.NetworkPrefabsLists.Add(list);

            var menu = new GameObject("Menü");
            menu.AddComponent<NetworkMenu>();
        }

        static void BuildMenuCamera()
        {
            var go = new GameObject("Menü Kamerası");
            go.transform.position = new Vector3(8f, 6f, -14f);
            go.transform.LookAt(new Vector3(0f, 1f, 10f));
            var cam = go.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            go.AddComponent<AudioListener>();
            go.AddComponent<MenuCamera>();
        }

        // ---------- Oyuncu ----------

        static GameObject BuildPlayerPrefab()
        {
            var player = new GameObject("Oyuncu");
            player.transform.position = Vector3.zero;

            var cc = player.AddComponent<CharacterController>();
            cc.height = 1.8f;
            cc.radius = 0.35f;
            cc.center = new Vector3(0f, 0.9f, 0f);
            cc.stepOffset = 0.35f;
            cc.slopeLimit = 50f;

            player.AddComponent<NetworkObject>();
            var nt = player.AddComponent<NetworkTransform>();
            nt.AuthorityMode = NetworkTransform.AuthorityModes.Owner;
            nt.SyncRotAngleX = false;
            nt.SyncRotAngleZ = false;
            nt.SyncScaleX = false;
            nt.SyncScaleY = false;
            nt.SyncScaleZ = false;
            nt.Interpolate = true;

            player.AddComponent<PlayerInputReader>();
            player.AddComponent<PlayerMotor>();
            var look = player.AddComponent<PlayerLook>();
            var inventory = player.AddComponent<PlayerInventory>();
            var interactor = player.AddComponent<PlayerInteractor>();
            var flashlight = player.AddComponent<Flashlight>();
            player.AddComponent<PlayerHUD>();
            var net = player.AddComponent<PlayerNetwork>();

            // Kamera
            var camGo = new GameObject("Kamera");
            camGo.transform.SetParent(player.transform, false);
            camGo.transform.localPosition = new Vector3(0f, 1.65f, 0f);
            var cam = camGo.AddComponent<Camera>();
            cam.nearClipPlane = 0.05f;
            cam.fieldOfView = 70f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            var listener = camGo.AddComponent<AudioListener>();
            camGo.tag = "MainCamera";

            // El feneri
            var lightGo = new GameObject("El Feneri");
            lightGo.transform.SetParent(camGo.transform, false);
            lightGo.transform.localPosition = new Vector3(0.2f, -0.15f, 0.1f);
            var spot = lightGo.AddComponent<Light>();
            spot.type = LightType.Spot;
            spot.range = 25f;
            spot.spotAngle = 55f;
            spot.innerSpotAngle = 25f;
            spot.intensity = 6f;
            spot.color = new Color(1f, 0.95f, 0.85f);
            spot.shadows = LightShadows.Soft;

            // El (tutulan eşyanın durduğu yer)
            var hand = new GameObject("El");
            hand.transform.SetParent(camGo.transform, false);
            hand.transform.localPosition = new Vector3(0.35f, -0.3f, 0.55f);

            // Gövde (diğer oyuncuların gördüğü kapsül, collider yok)
            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.name = "Gövde";
            body.transform.SetParent(player.transform, false);
            body.transform.localPosition = new Vector3(0f, 0.9f, 0f);
            body.transform.localScale = new Vector3(0.7f, 0.9f, 0.7f);
            Object.DestroyImmediate(body.GetComponent<Collider>());
            var bodyRenderer = body.GetComponent<Renderer>();
            bodyRenderer.sharedMaterial = Mat("Oyuncu Gövde", new Color(0.25f, 0.4f, 0.6f));

            look.cameraPivot = camGo.transform;
            inventory.viewPoint = camGo.transform;
            inventory.hand = hand.transform;
            interactor.viewPoint = camGo.transform;
            flashlight.spot = spot;
            net.cameraPivot = camGo.transform;
            net.body = body.transform;
            net.bodyRenderer = bodyRenderer;
            net.cam = cam;
            net.listener = listener;

            var prefab = PrefabUtility.SaveAsPrefabAsset(player, PlayerPrefabPath);
            Object.DestroyImmediate(player);
            return prefab;
        }

        // ---------- Yardımcılar ----------

        static GameObject Box(string name, Transform parent, Vector3 localPos, Vector3 size, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = size;
            go.GetComponent<Renderer>().sharedMaterial = mat;
            go.isStatic = true;
            return go;
        }

        static Material Mat(string name, Color color)
        {
            string safe = name.Replace(' ', '_');
            string path = $"{MaterialDir}/{safe}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, path);
            }
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            else mat.color = color;
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static void AddToBuildSettings(string path)
        {
            var list = new System.Collections.Generic.List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            list.RemoveAll(s => s.path == path);
            list.Insert(0, new EditorBuildSettingsScene(path, true));
            EditorBuildSettings.scenes = list.ToArray();
        }
    }
}
