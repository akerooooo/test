using System.Collections.Generic;
using System.IO;
using MimicGame.Environment;
using MimicGame.Game;
using MimicGame.Items;
using MimicGame.Level;
using MimicGame.Mimic;
using MimicGame.Network;
using MimicGame.Player;
using Netcode.Transports.Facepunch;
using MimicGame.UI;
using MimicGame.Voice;
using Unity.AI.Navigation;
using Unity.Netcode;
using Unity.Netcode.Components;
using Unity.Netcode.Transports.UTP;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace MimicGame.EditorTools
{
    /// <summary>
    /// Menü: MimicGame → Test Sahnesini Oluştur
    /// Oyuncu, araç (ganimet alanı), kapalı bir tesis cephesi (kapıdan prosedürel iç mekâna ışınlanılır),
    /// gün batımı atmosferi, radyo istasyonu ögeleri ve ganimet prefabları ile test sahnesi kurar;
    /// Assets/_Project/Scenes/TestScene.unity olarak kaydeder.
    /// </summary>
    public static class TestSceneBuilder
    {
        const string SceneDir = "Assets/_Project/Scenes";
        const string ScenePath = SceneDir + "/TestScene.unity";
        const string MaterialDir = "Assets/_Project/Materials";
        const string PrefabDir = "Assets/_Project/Prefabs";
        const string ItemPrefabDir = PrefabDir + "/Items";
        const string SettingsDir = "Assets/_Project/Settings";
        const string PostProfilePath = SettingsDir + "/PostProfile.asset";
        const string LootTablePath = SettingsDir + "/LootTable.asset";
        const string ModelsRoot = "Assets/ThirdParty/Models";
        const string PlayerPrefabPath = PrefabDir + "/Player.prefab";
        const string MimicPrefabPath = PrefabDir + "/Mimic.prefab";
        const string NetworkPrefabsListPath = "Assets/DefaultNetworkPrefabs.asset";

        static readonly Vector3 FacilityCenter = new Vector3(0f, 0f, 20f);

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
            Directory.CreateDirectory(ItemPrefabDir);
            Directory.CreateDirectory(SettingsDir);
            AssetDatabase.Refresh();

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            SetupLighting();
            BuildGround();
            BuildVan(new Vector3(0f, 0f, -8f));
            BuildFacilityExterior(FacilityCenter);
            BuildRadioStation();
            BuildLampPosts();
            BuildPostProcessing();
            var itemPrefabs = BuildItemPrefabs(out var lootTable);
            BuildFacilityGenerator(lootTable);
            BuildAtmosphere();
            var playerPrefab = BuildPlayerPrefab();
            var mimicPrefab = BuildMimicPrefab();
            BuildNavMesh();
            BuildMimicDirector(mimicPrefab);
            BuildNetwork(playerPrefab, mimicPrefab, itemPrefabs);
            BuildMenuCamera();

            EditorSceneManager.SaveScene(scene, ScenePath);
            RefreshNetworkObjectIds();
            AddToBuildSettings(ScenePath);
            Debug.Log("Test sahnesi oluşturuldu: " + ScenePath + " — Play'e basıp deneyebilirsin.");
        }

        // ---------- Işık ve atmosfer ----------

        static void SetupLighting()
        {
            // Ay / alacakaranlık ışığı: soğuk mavi, alçak açı (prosedürel gökyüzü de bu yönü kullanır)
            var moon = new GameObject("Ay Işığı");
            var l = moon.AddComponent<Light>();
            l.type = LightType.Directional;
            l.intensity = 0.35f;
            l.color = new Color(0.55f, 0.62f, 0.9f);
            l.shadows = LightShadows.Soft;
            moon.transform.rotation = Quaternion.Euler(22f, -40f, 0f);

            // Koyu mor-mavi alacakaranlık gökyüzü
            RenderSettings.skybox = BuildSkyMaterial();
            RenderSettings.sun = l;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.07f, 0.075f, 0.16f);
            RenderSettings.ambientEquatorColor = new Color(0.05f, 0.05f, 0.09f);
            RenderSettings.ambientGroundColor = new Color(0.015f, 0.015f, 0.02f);
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogDensity = 0.025f;
            RenderSettings.fogColor = new Color(0.06f, 0.07f, 0.1f);
        }

        static Material BuildSkyMaterial()
        {
            string path = MaterialDir + "/Gökyüzü.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Skybox/Procedural"));
                AssetDatabase.CreateAsset(mat, path);
            }
            mat.SetFloat("_SunSize", 0f);
            mat.SetFloat("_SunDisk", 0f); // güneş diski yok
            mat.DisableKeyword("_SUNDISK_SIMPLE");
            mat.DisableKeyword("_SUNDISK_HIGH_QUALITY");
            mat.EnableKeyword("_SUNDISK_NONE");
            mat.SetFloat("_AtmosphereThickness", 0.6f);
            mat.SetColor("_SkyTint", new Color(0.3f, 0.22f, 0.5f));
            mat.SetColor("_GroundColor", new Color(0.02f, 0.02f, 0.035f));
            mat.SetFloat("_Exposure", 0.35f);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        // Küresel Volume: tonemapping, bloom, renk, vinyet, film greni
        static void BuildPostProcessing()
        {
            AssetDatabase.DeleteAsset(PostProfilePath);
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            AssetDatabase.CreateAsset(profile, PostProfilePath);

            var tone = profile.Add<Tonemapping>(true);
            tone.mode.Override(TonemappingMode.ACES);

            var bloom = profile.Add<Bloom>(true);
            bloom.threshold.Override(0.9f);
            bloom.intensity.Override(0.6f);
            bloom.scatter.Override(0.7f);

            var color = profile.Add<ColorAdjustments>(true);
            color.postExposure.Override(0.2f);
            color.contrast.Override(10f);
            color.saturation.Override(-5f);
            color.colorFilter.Override(new Color(1f, 0.96f, 0.9f)); // hafif sıcak

            var vignette = profile.Add<Vignette>(true);
            vignette.intensity.Override(0.3f);

            var grain = profile.Add<FilmGrain>(true);
            grain.type.Override(FilmGrainLookup.Medium1);
            grain.intensity.Override(0.25f);

            // Bileşenler profilin alt varlığı olarak kaydedilmeli
            foreach (var c in profile.components)
                AssetDatabase.AddObjectToAsset(c, profile);
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();

            var go = new GameObject("Post-Processing");
            var volume = go.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 1f;
            volume.sharedProfile = profile;
        }

        static void EnablePostProcessing(Camera cam)
        {
            var data = cam.GetComponent<UniversalAdditionalCameraData>();
            if (data == null) data = cam.gameObject.AddComponent<UniversalAdditionalCameraData>();
            data.renderPostProcessing = true;
        }

        // ---------- Zemin ----------

        static void BuildGround()
        {
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Zemin";
            ground.transform.localScale = new Vector3(10f, 1f, 10f);
            ground.GetComponent<Renderer>().sharedMaterial = Mat("Zemin", new Color(0.1f, 0.11f, 0.1f));
            ground.isStatic = true;

            // Araçtan tesis kapısına beton yol
            var path = Mat("Beton Yol", new Color(0.17f, 0.17f, 0.18f));
            Box("Yol", null, new Vector3(0f, 0.015f, 1.75f), new Vector3(3.2f, 0.03f, 18.5f), path);
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

            BuildChargingStation(t);
        }

        // Kasa içinde ön duvara dayalı fener şarj istasyonu
        static void BuildChargingStation(Transform van)
        {
            var root = Box("Şarj İstasyonu", van, new Vector3(0f, 1.0f, -2.22f), new Vector3(1f, 1.4f, 0.4f),
                Mat("Şarj Kabin", new Color(0.18f, 0.2f, 0.22f)));
            Deco("Şarj Paneli", van, new Vector3(0f, 1.15f, -1.96f), new Vector3(0.7f, 0.3f, 0.05f),
                EmissiveMat("Şarj Panel", new Color(0.1f, 0.3f, 0.35f), new Color(0.2f, 0.9f, 1f) * 1.2f));
            var led = Deco("Durum Işığı", van, new Vector3(0f, 0.8f, -1.96f), new Vector3(0.2f, 0.12f, 0.05f),
                EmissiveMat("Şarj Durum", Color.white, Color.white));

            var glowGo = new GameObject("Durum Parıltısı");
            glowGo.transform.SetParent(van, false);
            glowGo.transform.localPosition = new Vector3(0f, 0.8f, -1.7f);
            var glow = glowGo.AddComponent<Light>();
            glow.type = LightType.Point;
            glow.range = 2f;
            glow.intensity = 0.6f;
            glow.color = new Color(1f, 0.15f, 0.1f);

            var st = root.AddComponent<ChargingStation>();
            st.statusLight = led.GetComponent<Renderer>();
            st.statusGlow = glow;
        }

        // ---------- Tesis cephesi (kapalı blok; iç mekân prosedürel ve haritanın altında) ----------

        static void BuildFacilityExterior(Vector3 c)
        {
            var root = new GameObject("Bina");
            root.transform.position = c;
            var t = root.transform;
            var wall = Mat("Dış Duvar Pas", new Color(0.26f, 0.2f, 0.16f));
            var band = Mat("Dış Duvar Bant", new Color(0.16f, 0.15f, 0.15f));
            var concrete = Mat("Beton", new Color(0.2f, 0.2f, 0.21f));
            var yellow = Mat("Sarı Çizgi", new Color(0.85f, 0.68f, 0.08f));
            var metal = Mat("Tesis Metal", new Color(0.24f, 0.26f, 0.27f));
            var doorMat = Mat("Ana Kapı", new Color(0.14f, 0.2f, 0.17f));
            var warm = EmissiveMat("Sıcak Pencere", new Color(0.3f, 0.2f, 0.1f), new Color(1.6f, 0.9f, 0.4f));
            var sodium = EmissiveMat("Sodyum", new Color(0.9f, 0.5f, 0.2f), new Color(3f, 1.6f, 0.5f));

            // Ana blok: 20 x 8 x 20, yürünebilir iç mekânı yok
            Box("Ana Gövde", t, new Vector3(0f, 4f, 0f), new Vector3(20f, 8f, 20f), wall);
            Box("Kaide", t, new Vector3(0f, 0.15f, 0f), new Vector3(20.6f, 0.3f, 20.6f), concrete);
            Deco("Üst Bant", t, new Vector3(0f, 6.6f, 0f), new Vector3(20.15f, 0.7f, 20.15f), band);
            Deco("Alt Bant", t, new Vector3(0f, 1.6f, 0f), new Vector3(20.1f, 0.5f, 20.1f), band);

            // Çatı korkuluğu ve çatı eşyaları
            Box("Korkuluk Kuzey", t, new Vector3(0f, 8.3f, 9.85f), new Vector3(20f, 0.6f, 0.3f), concrete);
            Box("Korkuluk Güney", t, new Vector3(0f, 8.3f, -9.85f), new Vector3(20f, 0.6f, 0.3f), concrete);
            Box("Korkuluk Doğu", t, new Vector3(9.85f, 8.3f, 0f), new Vector3(0.3f, 0.6f, 20f), concrete);
            Box("Korkuluk Batı", t, new Vector3(-9.85f, 8.3f, 0f), new Vector3(0.3f, 0.6f, 20f), concrete);
            Deco("Klima Ünitesi", t, new Vector3(5f, 8.7f, 3f), new Vector3(3f, 1.4f, 2f), metal);
            Deco("Klima Ünitesi 2", t, new Vector3(-4f, 8.6f, -2f), new Vector3(2.5f, 1.2f, 2f), metal);
            Cylinder("Baca", t, new Vector3(-6f, 9.5f, 5f), new Vector3(0.9f, 1.5f, 0.9f), metal, false);

            // Ana kapı (güney yüz, z = 10 dünya konumunda). Boyut ve görünüm iç mekândaki çıkış kapısıyla aynı:
            // FacilityDoorKit (2.4 x 3.0 m çift kanat metal kapı, aynı çerçeve, malzeme ve çıkış lambası)
            float fz = -10f;
            Box("Kapı Direk Sol", t, new Vector3(-1.6f, 1.8f, fz - 0.2f), new Vector3(0.5f, 3.6f, 0.5f), concrete);
            Box("Kapı Direk Sağ", t, new Vector3(1.6f, 1.8f, fz - 0.2f), new Vector3(0.5f, 3.6f, 0.5f), concrete);
            Deco("Kapı Üst Kiriş", t, new Vector3(0f, 3.3f, fz - 0.2f), new Vector3(4.0f, 0.6f, 0.5f), concrete);
            Deco("Sundurma", t, new Vector3(0f, 4.5f, fz - 1.4f), new Vector3(6f, 0.2f, 2.8f), metal);
            Deco("Uyarı Şeridi Sol", t, new Vector3(-2.35f, 1.7f, fz - 0.47f), new Vector3(0.25f, 3.4f, 0.04f), yellow);
            Deco("Uyarı Şeridi Sağ", t, new Vector3(2.35f, 1.7f, fz - 0.47f), new Vector3(0.25f, 3.4f, 0.04f), yellow);

            // Kit'in yerel +Z yönü kapının önüdür (cephede güney = -Z), bu yüzden 180° döner
            var doorKit = FacilityDoorKit.Build(t, "Ana Kapı", new Vector3(0f, 0f, fz - 0.1f), 180f, FacilityDoorMats(), false, 0.45f);
            foreach (var tr in doorKit.GetComponentsInChildren<Transform>()) tr.gameObject.isStatic = true;

            var lampGo = new GameObject("Kapı Üstü Lamba Işığı");
            lampGo.transform.SetParent(t, false);
            lampGo.transform.localPosition = new Vector3(0f, 4.1f, fz - 0.8f);
            lampGo.transform.localRotation = Quaternion.Euler(60f, 180f, 0f); // güneye ve aşağıya
            var spot = lampGo.AddComponent<Light>();
            spot.type = LightType.Spot;
            spot.color = new Color(1f, 0.65f, 0.3f);
            spot.intensity = 4f;
            spot.range = 12f;
            spot.spotAngle = 110f;
            spot.innerSpotAngle = 60f;
            spot.shadows = LightShadows.Soft;

            // Sönük sıcak pencere yarıkları
            for (int i = 0; i < 4; i++)
                Deco("Pencere " + (i + 1), t, new Vector3(-7.5f + i * 5f, 6.2f, fz - 0.1f), new Vector3(1.4f, 0.35f, 0.06f), warm);
        }

        // ---------- Radyo istasyonu: uydu çanağı + kafes direk + uçak ikaz ışıkları ----------

        static void BuildRadioStation()
        {
            var root = new GameObject("Radyo İstasyonu");
            var t = root.transform;
            var metal = Mat("Çanak Metal", new Color(0.35f, 0.34f, 0.32f));
            var rust = Mat("Kafes Pas", new Color(0.3f, 0.18f, 0.12f));
            var red = EmissiveMat("Uçak Işığı", new Color(0.4f, 0.03f, 0.03f), new Color(4f, 0.2f, 0.15f));

            // Büyük çanak (tesisin arkasında)
            Vector3 b = new Vector3(-6f, 0f, 38f);
            Cylinder("Çanak Direği", t, b + new Vector3(0f, 3f, 0f), new Vector3(0.5f, 3f, 0.5f), metal, true);
            Deco("Çanak Tabanı", t, b + new Vector3(0f, 0.2f, 0f), new Vector3(2f, 0.4f, 2f), metal);

            var pivot = new GameObject("Çanak");
            pivot.transform.SetParent(t, false);
            pivot.transform.position = b + new Vector3(0f, 6.4f, 0f);
            pivot.transform.rotation = Quaternion.Euler(-55f, 30f, 0f); // gökyüzüne bakar
            var pt = pivot.transform;

            var dish = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            dish.name = "Çanak Tabağı";
            Object.DestroyImmediate(dish.GetComponent<Collider>());
            dish.transform.SetParent(pt, false);
            dish.transform.localScale = new Vector3(7f, 0.9f, 7f); // yassı küre
            dish.GetComponent<Renderer>().sharedMaterial = metal;

            Vector3 focus = new Vector3(0f, 3.2f, 0f);
            Strut("Besleme Kolu", pt, new Vector3(3.3f, 0.25f, 0f), focus, 0.08f, rust);
            Strut("Besleme Kolu", pt, new Vector3(-3.3f, 0.25f, 0f), focus, 0.08f, rust);
            Strut("Besleme Kolu", pt, new Vector3(0f, 0.25f, 3.3f), focus, 0.08f, rust);
            Strut("Besleme Kolu", pt, new Vector3(0f, 0.25f, -3.3f), focus, 0.08f, rust);
            Strut("Arka Kafes", pt, new Vector3(-3.2f, -0.2f, 0f), new Vector3(3.2f, -0.2f, 0f), 0.1f, rust);
            Strut("Arka Kafes", pt, new Vector3(0f, -0.2f, -3.2f), new Vector3(0f, -0.2f, 3.2f), 0.1f, rust);
            Strut("Çanak Desteği", pt, new Vector3(0f, -0.3f, 0f), new Vector3(0f, -1.0f, 0f), 0.2f, rust);
            Cylinder("Besleme Kornası", pt, focus, new Vector3(0.3f, 0.2f, 0.3f), metal, false);

            // Çanağın üstündeki küçük kırmızı ikaz ışığı
            MakeBlinker("Çanak İkaz Işığı", pt, focus + new Vector3(0f, 0.35f, 0f), red);

            // Kafes direk
            Vector3 m = new Vector3(11f, 0f, 38f);
            const int levels = 9;
            const float step = 3f;
            Vector3[] prev = null;
            for (int k = 0; k <= levels; k++)
            {
                float r = 0.95f - 0.05f * k;
                var ring = new Vector3[3];
                for (int i = 0; i < 3; i++)
                {
                    float ang = i * 120f * Mathf.Deg2Rad;
                    ring[i] = m + new Vector3(Mathf.Cos(ang) * r, k * step, Mathf.Sin(ang) * r);
                }
                for (int i = 0; i < 3; i++)
                    Strut("Kafes Yatay", t, ring[i], ring[(i + 1) % 3], 0.07f, rust);
                if (prev != null)
                    for (int i = 0; i < 3; i++)
                    {
                        Strut("Kafes Bacak", t, prev[i], ring[i], 0.1f, rust);
                        Strut("Kafes Çapraz", t, prev[i], ring[(i + 1) % 3], 0.05f, rust);
                    }
                prev = ring;
            }
            // Direğin tabanına tek çarpıştırıcı, tepeye anten ve ikaz ışığı
            Box("Direk Tabanı", t, m + new Vector3(0f, 0.4f, 0f), new Vector3(2.4f, 0.8f, 2.4f), Mat("Beton", new Color(0.2f, 0.2f, 0.21f)));
            float topY = levels * step;
            Cylinder("Anten", t, m + new Vector3(0f, topY + 1.5f, 0f), new Vector3(0.08f, 1.5f, 0.08f), metal, false);
            MakeBlinker("Direk İkaz Işığı", t, m + new Vector3(0f, topY + 3.2f, 0f), red);
        }

        // Kırmızı küçük küre + nokta ışık; BlinkLight 1 sn periyot
        static void MakeBlinker(string name, Transform parent, Vector3 pos, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = name;
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = Vector3.one * 0.25f;
            var rend = go.GetComponent<Renderer>();
            rend.sharedMaterial = mat;
            rend.shadowCastingMode = ShadowCastingMode.Off;

            var l = go.AddComponent<Light>();
            l.type = LightType.Point;
            l.color = new Color(1f, 0.1f, 0.08f);
            l.intensity = 3f;
            l.range = 10f;
            l.shadows = LightShadows.None;

            var blink = go.AddComponent<BlinkLight>();
            blink.period = 1f;
            blink.lens = rend;
        }

        // ---------- Sodyum lamba direkleri ----------

        static void BuildLampPosts()
        {
            var root = new GameObject("Lamba Direkleri");
            var pole = Mat("Direk Metal", new Color(0.15f, 0.15f, 0.16f));
            var sodium = EmissiveMat("Sodyum", new Color(0.9f, 0.5f, 0.2f), new Color(3f, 1.6f, 0.5f));

            // Araç çevresi (gölgesiz) ve giriş (yalnızca bu ikisi gölgeli)
            LampPost(root.transform, new Vector3(-6f, 0f, -4f), pole, sodium, false, false);
            LampPost(root.transform, new Vector3(7f, 0f, -12f), pole, sodium, false, false);
            LampPost(root.transform, new Vector3(-5.5f, 0f, 3f), pole, sodium, false, true); // titreyen
            LampPost(root.transform, new Vector3(5.5f, 0f, 3f), pole, sodium, false, false);
            LampPost(root.transform, new Vector3(-6f, 0f, 8f), pole, sodium, true, false);
            LampPost(root.transform, new Vector3(6f, 0f, 8f), pole, sodium, true, false);
        }

        static void LampPost(Transform parent, Vector3 pos, Material poleMat, Material bulbMat, bool shadows, bool flicker)
        {
            var go = new GameObject("Lamba Direği");
            go.transform.SetParent(parent, false);
            go.transform.position = pos;
            var t = go.transform;

            Cylinder("Direk", t, new Vector3(0f, 2.5f, 0f), new Vector3(0.12f, 2.5f, 0.12f), poleMat, true);
            Deco("Kol", t, new Vector3(0.4f, 5.0f, 0f), new Vector3(0.9f, 0.08f, 0.08f), poleMat);
            Deco("Başlık", t, new Vector3(0.85f, 4.95f, 0f), new Vector3(0.55f, 0.12f, 0.32f), poleMat);
            Deco("Ampul", t, new Vector3(0.85f, 4.87f, 0f), new Vector3(0.4f, 0.05f, 0.22f), bulbMat);

            var lightGo = new GameObject("Sodyum Işığı");
            lightGo.transform.SetParent(t, false);
            lightGo.transform.localPosition = new Vector3(0.85f, 4.6f, 0f);
            var l = lightGo.AddComponent<Light>();
            l.type = LightType.Point;
            l.color = new Color(1f, 0.65f, 0.3f);
            l.intensity = 3f;
            l.range = 14f;
            l.shadows = shadows ? LightShadows.Soft : LightShadows.None;
            if (flicker) lightGo.AddComponent<LightFlicker>();
        }

        // ---------- Tesis iç mekân üreticisi (ağ nesnesi) ----------

        static void BuildFacilityGenerator(LootTable table)
        {
            var go = new GameObject("Tesis Üreteci");
            go.transform.position = new Vector3(0f, -100f, 0f); // haritanın çok altı
            go.AddComponent<NetworkObject>(); // sahnede yerleşik; kimlikleri RefreshNetworkObjectIds verir
            var gen = go.AddComponent<FacilityGenerator>();
            gen.lootTable = table;
            gen.outsideExitPoint = new Vector3(0f, 0.05f, 7.5f);
            gen.outsideExitYaw = 180f;

            gen.concrete = Mat("Tesis Beton", new Color(0.17f, 0.17f, 0.18f));
            gen.ceilingMat = Mat("Tesis Tavan", new Color(0.1f, 0.1f, 0.11f));
            gen.lineMat = Mat("Sarı Çizgi", new Color(0.85f, 0.68f, 0.08f));
            gen.rustMat = Mat("Tesis Pas Metal", new Color(0.3f, 0.16f, 0.1f));
            gen.crateMat = Mat("Tesis Sandık", new Color(0.33f, 0.24f, 0.13f));
            gen.deskMat = Mat("Tesis Ahşap", new Color(0.22f, 0.2f, 0.18f));
            gen.pipeMat = Mat("Tesis Boru", new Color(0.25f, 0.2f, 0.17f));
            gen.cableMat = Mat("Tesis Kablo", new Color(0.03f, 0.03f, 0.03f));
            gen.paperMat = Mat("Tesis Kağıt", new Color(0.7f, 0.68f, 0.6f));
            gen.fabricMat = Mat("Tesis Kumaş", new Color(0.18f, 0.2f, 0.17f));
            gen.blackMat = Mat("Tesis Siyah", new Color(0.04f, 0.04f, 0.045f));
            gen.screenMat = EmissiveMat("Tesis Ekran", new Color(0.03f, 0.1f, 0.08f), new Color(0.2f, 1.2f, 0.9f));
            gen.screenAmberMat = EmissiveMat("Tesis Ekran Amber", new Color(0.15f, 0.08f, 0.02f), new Color(1.3f, 0.7f, 0.15f));
            gen.stripOnMat = EmissiveMat("Tesis Floresan Açık", new Color(0.8f, 0.9f, 1f), new Color(2f, 2.2f, 2.4f));
            gen.stripOffMat = Mat("Tesis Floresan Kapalı", new Color(0.08f, 0.08f, 0.09f));
            gen.ledGreenMat = EmissiveMat("Tesis LED Yeşil", new Color(0.05f, 0.3f, 0.05f), new Color(0.2f, 2f, 0.3f));
            gen.ledRedMat = EmissiveMat("Tesis LED Kırmızı", new Color(0.3f, 0.05f, 0.05f), new Color(2f, 0.2f, 0.15f));
            gen.ledAmberMat = EmissiveMat("Tesis LED Amber", new Color(0.3f, 0.2f, 0.03f), new Color(2f, 1.2f, 0.2f));

            // Cephe kapısıyla ortak malzemeler (iki kapı birebir aynı görünür)
            var dm = FacilityDoorMats();
            gen.doorMat = dm.panel;
            gen.doorFrameMat = dm.frame;
            gen.metalMat = dm.metal;
            gen.exitSignMat = dm.lamp;
        }

        // Cephe ve iç mekân çıkış kapısının ortak malzemeleri
        static FacilityDoorMaterials FacilityDoorMats()
        {
            return new FacilityDoorMaterials
            {
                panel = Mat("Tesis Çıkış Kapısı", new Color(0.3f, 0.16f, 0.1f)),
                frame = Mat("Tesis Çerçeve", new Color(0.08f, 0.08f, 0.09f)),
                metal = Mat("Tesis Metal", new Color(0.24f, 0.26f, 0.27f)),
                lamp = EmissiveMat("Tesis Çıkış Levhası", new Color(0.05f, 0.3f, 0.1f), new Color(0.2f, 2f, 0.5f))
            };
        }

        static void BuildAtmosphere()
        {
            var go = new GameObject("Atmosfer");
            go.AddComponent<AtmosphereController>();
        }

        // ---------- Ganimet prefabları ----------

        sealed class ItemDef
        {
            public string name;
            public int value;
            public float weight;
            public PrimitiveType shape;
            public Color color;
            public Vector3 scale;
            public float spawnWeight;

            public ItemDef(string name, int value, float weight, PrimitiveType shape, Color color, Vector3 scale, float spawnWeight)
            {
                this.name = name; this.value = value; this.weight = weight; this.shape = shape;
                this.color = color; this.scale = scale; this.spawnWeight = spawnWeight;
            }

            /// <summary>Şeklin dünya boyutu (silindir ilkeli 2 birim yüksekliğindedir).</summary>
            public Vector3 Dimensions =>
                shape == PrimitiveType.Cylinder || shape == PrimitiveType.Capsule
                    ? new Vector3(scale.x, scale.y * 2f, scale.z)
                    : scale;
        }

        static ItemDef[] ItemDefs() => new[]
        {
            new ItemDef("Eski Saat", 25, 1f, PrimitiveType.Cylinder, new Color(0.8f, 0.7f, 0.3f), new Vector3(0.3f, 0.05f, 0.3f), 8f),
            new ItemDef("Bakır Boru", 15, 6f, PrimitiveType.Cylinder, new Color(0.75f, 0.4f, 0.2f), new Vector3(0.12f, 0.6f, 0.12f), 8f),
            new ItemDef("Kırık Radyo", 40, 8f, PrimitiveType.Cube, new Color(0.3f, 0.25f, 0.2f), new Vector3(0.5f, 0.3f, 0.25f), 5f),
            new ItemDef("Oyuncak Ayı", 12, 1f, PrimitiveType.Sphere, new Color(0.6f, 0.4f, 0.25f), new Vector3(0.35f, 0.35f, 0.35f), 8f),
            new ItemDef("Altın Kupa", 80, 5f, PrimitiveType.Cylinder, new Color(1f, 0.8f, 0.1f), new Vector3(0.3f, 0.25f, 0.3f), 2f),
            new ItemDef("Teneke Kutu", 5, 1f, PrimitiveType.Cylinder, new Color(0.6f, 0.6f, 0.6f), new Vector3(0.15f, 0.12f, 0.15f), 12f),
            new ItemDef("Fotoğraf Makinesi", 55, 2f, PrimitiveType.Cube, new Color(0.1f, 0.1f, 0.1f), new Vector3(0.3f, 0.2f, 0.15f), 4f),
            new ItemDef("Akü", 30, 15f, PrimitiveType.Cube, new Color(0.15f, 0.3f, 0.15f), new Vector3(0.45f, 0.35f, 0.3f), 4f),
            new ItemDef("Gümüş Tabak", 35, 2f, PrimitiveType.Cylinder, new Color(0.85f, 0.85f, 0.9f), new Vector3(0.4f, 0.02f, 0.4f), 5f),
            new ItemDef("Eski Telefon", 20, 1f, PrimitiveType.Cube, new Color(0.5f, 0.1f, 0.1f), new Vector3(0.12f, 0.06f, 0.25f), 8f),
            // Radyo istasyonu temalı
            new ItemDef("Radyo Lambası", 45, 1f, PrimitiveType.Cylinder, new Color(0.75f, 0.85f, 0.9f), new Vector3(0.09f, 0.12f, 0.09f), 6f),
            new ItemDef("Kaset", 10, 1f, PrimitiveType.Cube, new Color(0.12f, 0.1f, 0.1f), new Vector3(0.2f, 0.03f, 0.12f), 10f),
            new ItemDef("Bant Makarası", 18, 2f, PrimitiveType.Cylinder, new Color(0.2f, 0.2f, 0.22f), new Vector3(0.3f, 0.03f, 0.3f), 7f),
            new ItemDef("Kulaklık", 22, 1f, PrimitiveType.Cube, new Color(0.1f, 0.1f, 0.12f), new Vector3(0.22f, 0.1f, 0.2f), 7f),
            new ItemDef("Mikrofon", 35, 2f, PrimitiveType.Cylinder, new Color(0.7f, 0.7f, 0.75f), new Vector3(0.07f, 0.13f, 0.07f), 5f),
            new ItemDef("Frekans Kadranı", 28, 3f, PrimitiveType.Cylinder, new Color(0.6f, 0.5f, 0.3f), new Vector3(0.24f, 0.04f, 0.24f), 6f),
            new ItemDef("Devre Kartı", 15, 1f, PrimitiveType.Cube, new Color(0.1f, 0.4f, 0.15f), new Vector3(0.28f, 0.02f, 0.2f), 9f),
            new ItemDef("Osiloskop", 90, 12f, PrimitiveType.Cube, new Color(0.2f, 0.3f, 0.25f), new Vector3(0.5f, 0.35f, 0.4f), 2f),
        };

        static List<GameObject> BuildItemPrefabs(out LootTable table)
        {
            var defs = ItemDefs();
            var prefabs = new List<GameObject>();
            var entries = new List<LootTable.Entry>();
            foreach (var d in defs)
            {
                var prefab = BuildItemPrefab(d);
                prefabs.Add(prefab);
                entries.Add(new LootTable.Entry { prefab = prefab, weight = d.spawnWeight });
            }

            table = AssetDatabase.LoadAssetAtPath<LootTable>(LootTablePath);
            if (table == null)
            {
                table = ScriptableObject.CreateInstance<LootTable>();
                AssetDatabase.CreateAsset(table, LootTablePath);
            }
            table.entries = entries.ToArray();
            EditorUtility.SetDirty(table);
            AssetDatabase.SaveAssets();
            return prefabs;
        }

        static GameObject BuildItemPrefab(ItemDef d)
        {
            string slug = Slug(d.name);
            if (ModelAliases.TryGetValue(slug, out var alias)) slug = alias;
            var root = new GameObject(d.name);
            Vector3 dims = d.Dimensions;
            Vector3 colliderSize = dims;

            // Gerçek model varsa ilkel şeklin yerine geçer
            var modelAsset = FindModel(slug);
            bool swapped = false;
            if (modelAsset != null)
            {
                var inst = (GameObject)PrefabUtility.InstantiatePrefab(modelAsset);
                inst.name = "Model";
                inst.transform.SetParent(root.transform, false);
                var rends = inst.GetComponentsInChildren<Renderer>();
                if (rends.Length > 0)
                {
                    Bounds b = rends[0].bounds;
                    for (int i = 1; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);
                    float maxModel = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
                    if (maxModel > 1e-5f)
                    {
                        float k = Mathf.Max(dims.x, Mathf.Max(dims.y, dims.z)) / maxModel;
                        inst.transform.localScale = Vector3.one * k;
                        inst.transform.localPosition = -b.center * k; // sınır merkezi köke otursun
                        colliderSize = b.size * k;
                        swapped = true;
                    }
                }
                if (!swapped) Object.DestroyImmediate(inst);
            }
            Debug.Log("[Model] " + slug + (swapped ? " bulundu" : " yok (ilkel şekil)"));

            if (!swapped)
            {
                var vis = GameObject.CreatePrimitive(d.shape);
                vis.name = "Görsel";
                Object.DestroyImmediate(vis.GetComponent<Collider>());
                vis.transform.SetParent(root.transform, false);
                vis.transform.localScale = d.scale;
                vis.GetComponent<Renderer>().sharedMaterial = Mat("Ganimet " + d.name, d.color);
            }

            // Küre/kapsül collider yerine kutu (yuvarlanmasın)
            var box = root.AddComponent<BoxCollider>();
            box.size = colliderSize;
            box.center = Vector3.zero;
            IgnoreNavMesh(root);

            // Eşya durumunu sunucu tutar; reparent yok
            var no = root.AddComponent<NetworkObject>();
            no.AutoObjectParentSync = false;

            var item = root.AddComponent<Item>();
            item.displayName = d.name;
            item.value = d.value;
            item.weight = d.weight;

            var swap = root.AddComponent<ModelSwap>();
            swap.modelName = slug;
            swap.hasModel = swapped;

            var prefab = PrefabUtility.SaveAsPrefabAsset(root, ItemPrefabDir + "/" + slug + ".prefab");
            Object.DestroyImmediate(root);
            return prefab;
        }

        // Assets/ThirdParty/Models/**/{slug}.fbx (tam dosya adı eşleşmesi)
        static GameObject FindModel(string slug)
        {
            if (!AssetDatabase.IsValidFolder(ModelsRoot)) return null;
            foreach (var guid in AssetDatabase.FindAssets(slug + " t:Model", new[] { ModelsRoot }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(Path.GetFileNameWithoutExtension(path), slug, System.StringComparison.OrdinalIgnoreCase)) continue;
                var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (go != null) return go;
            }
            return null;
        }

        /// <summary>Türkçe ad → ASCII snake_case ("Radyo Lambası" → "radyo_lambasi").</summary>
        // Model paketindeki dosya adı eşyanın adından farklıysa
        static readonly Dictionary<string, string> ModelAliases = new Dictionary<string, string>
        {
            { "fotograf_makinesi", "eski_fotograf_makinesi" },
            { "aku", "araba_akusu" },
            { "eski_telefon", "cevirmeli_telefon" },
        };

        static string Slug(string name)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char ch in name)
            {
                char c = ch;
                switch (c)
                {
                    case 'ç': case 'Ç': c = 'c'; break;
                    case 'ğ': case 'Ğ': c = 'g'; break;
                    case 'ı': case 'İ': case 'I': c = 'i'; break;
                    case 'ö': case 'Ö': c = 'o'; break;
                    case 'ş': case 'Ş': c = 's'; break;
                    case 'ü': case 'Ü': c = 'u'; break;
                    case ' ': c = '_'; break;
                }
                c = char.ToLowerInvariant(c);
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_') sb.Append(c);
            }
            return sb.ToString();
        }

        // ---------- Ağ ----------

        static void BuildNetwork(GameObject playerPrefab, GameObject mimicPrefab, List<GameObject> itemPrefabs)
        {
            var go = new GameObject("NetworkManager");
            var nm = go.AddComponent<NetworkManager>();
            var utp = go.AddComponent<UnityTransport>();
            go.AddComponent<FacepunchTransport>(); // Steam için; varsayılan UnityTransport kalır
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
            if (!list.Contains(mimicPrefab))
                list.Add(new NetworkPrefab { Prefab = mimicPrefab });
            foreach (var ip in itemPrefabs)
                if (ip != null && !list.Contains(ip))
                    list.Add(new NetworkPrefab { Prefab = ip });
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
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.backgroundColor = Color.black;
            EnablePostProcessing(cam);
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
            var voice = player.AddComponent<PlayerVoice>();
            player.AddComponent<ClipSharing>();
            var health = player.AddComponent<PlayerHealth>();

            // Kamera
            var camGo = new GameObject("Kamera");
            camGo.transform.SetParent(player.transform, false);
            camGo.transform.localPosition = new Vector3(0f, 1.65f, 0f);
            var cam = camGo.AddComponent<Camera>();
            cam.nearClipPlane = 0.05f;
            cam.fieldOfView = 70f;
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.backgroundColor = Color.black;
            EnablePostProcessing(cam);
            var listener = camGo.AddComponent<AudioListener>();
            camGo.tag = "MainCamera";

            // Gözlemci kamerası (varsayılan kapalı; sahibi ölünce PlayerHealth açar)
            var spectator = camGo.AddComponent<SpectatorCamera>();
            spectator.enabled = false;
            health.spectator = spectator;

            // El feneri
            var lightGo = new GameObject("El Feneri");
            lightGo.transform.SetParent(camGo.transform, false);
            lightGo.transform.localPosition = new Vector3(0.2f, -0.15f, 0.1f);
            var spot = lightGo.AddComponent<Light>();
            spot.type = LightType.Spot;
            spot.range = 30f;
            spot.spotAngle = 62f;
            spot.innerSpotAngle = 30f;
            spot.intensity = 9f;
            spot.color = new Color(1f, 0.96f, 0.88f);
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

            // Ses (ağız hizası; kamerayla birlikte hareket eder)
            var sesGo = new GameObject("Ses");
            sesGo.transform.SetParent(camGo.transform, false);
            sesGo.transform.localPosition = new Vector3(0f, -0.1f, 0f);
            var sesSource = sesGo.AddComponent<AudioSource>();
            sesSource.playOnAwake = false; // klip VoicePlayer tarafından oluşturulur
            sesGo.AddComponent<AudioLowPassFilter>();
            voice.voicePlayer = sesGo.AddComponent<VoicePlayer>();

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

        // ---------- Taklitçi ----------

        static GameObject BuildMimicPrefab()
        {
            var mimic = new GameObject("Taklitçi");
            mimic.transform.position = Vector3.zero;

            mimic.AddComponent<NetworkObject>();
            var nt = mimic.AddComponent<NetworkTransform>();
            nt.AuthorityMode = NetworkTransform.AuthorityModes.Server;
            nt.SyncRotAngleX = false;
            nt.SyncRotAngleZ = false;
            nt.SyncScaleX = false;
            nt.SyncScaleY = false;
            nt.SyncScaleZ = false;
            nt.Interpolate = true;

            var agent = mimic.AddComponent<NavMeshAgent>();
            agent.radius = 0.35f;
            agent.height = 1.8f;
            agent.speed = 3.5f;
            agent.angularSpeed = 720f;
            agent.acceleration = 20f;
            agent.stoppingDistance = 1.2f;
            agent.enabled = false; // yalnızca sunucuda MimicAI açar

            var col = mimic.AddComponent<CapsuleCollider>();
            col.height = 1.8f;
            col.radius = 0.35f;
            col.center = new Vector3(0f, 0.9f, 0f);

            var ai = mimic.AddComponent<MimicAI>();

            // Gövde: oyuncununkiyle aynı
            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.name = "Gövde";
            body.transform.SetParent(mimic.transform, false);
            body.transform.localPosition = new Vector3(0f, 0.9f, 0f);
            body.transform.localScale = new Vector3(0.7f, 0.9f, 0.7f);
            Object.DestroyImmediate(body.GetComponent<Collider>());
            var bodyRenderer = body.GetComponent<Renderer>();
            bodyRenderer.sharedMaterial = Mat("Oyuncu Gövde", new Color(0.25f, 0.4f, 0.6f));

            // Kamera yerine sadece pivot
            var pivot = new GameObject("Kamera");
            pivot.transform.SetParent(mimic.transform, false);
            pivot.transform.localPosition = new Vector3(0f, 1.65f, 0f);

            var lightGo = new GameObject("El Feneri");
            lightGo.transform.SetParent(pivot.transform, false);
            lightGo.transform.localPosition = new Vector3(0.2f, -0.15f, 0.1f);
            var spot = lightGo.AddComponent<Light>();
            spot.type = LightType.Spot;
            spot.range = 25f;
            spot.spotAngle = 55f;
            spot.innerSpotAngle = 25f;
            spot.intensity = 6f;
            spot.color = new Color(1f, 0.95f, 0.85f);
            spot.shadows = LightShadows.Soft;
            spot.enabled = false;

            var sesGo = new GameObject("Ses");
            sesGo.transform.SetParent(pivot.transform, false);
            sesGo.transform.localPosition = new Vector3(0f, -0.1f, 0f);
            var sesSource = sesGo.AddComponent<AudioSource>();
            sesSource.playOnAwake = false;
            sesGo.AddComponent<AudioLowPassFilter>();
            var vp = sesGo.AddComponent<VoicePlayer>();

            ai.body = body.transform;
            ai.bodyRenderer = bodyRenderer;
            ai.cameraPivot = pivot.transform;
            ai.spot = spot;
            ai.voicePlayer = vp;

            var prefab = PrefabUtility.SaveAsPrefabAsset(mimic, MimicPrefabPath);
            Object.DestroyImmediate(mimic);
            return prefab;
        }

        static void BuildNavMesh()
        {
            var go = new GameObject("NavMesh");
            var surface = go.AddComponent<NavMeshSurface>();
            surface.collectObjects = CollectObjects.All;
            surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
            var baker = go.AddComponent<RuntimeNavMeshBaker>();
            baker.surface = surface;
        }

        static void BuildMimicDirector(GameObject mimicPrefab)
        {
            var go = new GameObject("Taklitçi Yöneticisi");
            var director = go.AddComponent<MimicDirector>();
            director.mimicPrefab = mimicPrefab;
            // Doğma noktaları çalışma anında FacilityGenerator'dan gelir
            director.spawnPoints = new Transform[0];
        }

        static void IgnoreNavMesh(GameObject go)
        {
            var mod = go.AddComponent<NavMeshModifier>();
            mod.ignoreFromBuild = true;
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

        // Çarpıştırıcısız süs kutusu
        static GameObject Deco(string name, Transform parent, Vector3 localPos, Vector3 size, Material mat)
        {
            var go = Box(name, parent, localPos, size, mat);
            Object.DestroyImmediate(go.GetComponent<Collider>());
            return go;
        }

        static GameObject Cylinder(string name, Transform parent, Vector3 localPos, Vector3 scale, Material mat, bool collider)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = scale; // silindir ilkeli: 1 çap, 2 yükseklik
            go.GetComponent<Renderer>().sharedMaterial = mat;
            if (!collider) Object.DestroyImmediate(go.GetComponent<Collider>());
            go.isStatic = true;
            return go;
        }

        // İki yerel nokta arasında ince kiriş (kafes yapılar için), çarpıştırıcısız
        static GameObject Strut(string name, Transform parent, Vector3 a, Vector3 b, float thickness, Material mat)
        {
            Vector3 d = b - a;
            float len = d.magnitude;
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = (a + b) * 0.5f;
            if (len > 1e-4f) go.transform.localRotation = Quaternion.FromToRotation(Vector3.forward, d / len);
            go.transform.localScale = new Vector3(thickness, thickness, Mathf.Max(len, 0.001f));
            go.GetComponent<Renderer>().sharedMaterial = mat;
            go.isStatic = true;
            return go;
        }

        static Material EmissiveMat(string name, Color color, Color emission)
        {
            var mat = Mat(name, color);
            mat.EnableKeyword("_EMISSION");
            mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            if (mat.HasProperty("_EmissionColor")) mat.SetColor("_EmissionColor", emission);
            EditorUtility.SetDirty(mat);
            return mat;
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
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.2f); // mat, taş/pas görünümü
            EditorUtility.SetDirty(mat);
            return mat;
        }

        /// <summary>
        /// Sahnedeki NetworkObject kimlikleri (GlobalObjectIdHash) kaydedilmemiş sahnede doğru
        /// üretilemez. Sahneyi kaydettikten sonra yeniden açıp kimlikleri yenileyip tekrar kaydeder.
        /// </summary>
        static void RefreshNetworkObjectIds()
        {
            var reopened = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var onValidate = typeof(NetworkObject).GetMethod("OnValidate",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Public);
            foreach (var root in reopened.GetRootGameObjects())
            {
                foreach (var no in root.GetComponentsInChildren<NetworkObject>(true))
                {
                    onValidate?.Invoke(no, null);
                    EditorUtility.SetDirty(no);
                }
            }
            EditorSceneManager.MarkSceneDirty(reopened);
            EditorSceneManager.SaveScene(reopened);
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
