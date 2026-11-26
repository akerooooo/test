using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace MimicGame.EditorTools
{
    /// <summary>
    /// Assets/ThirdParty/Models altındaki FBX'lerin malzeme renklerini (DiffuseColor)
    /// URP Lit materyaline aktarır. Model paketinde doku yok; renkler malzemede saklı.
    /// </summary>
    public class ModelMaterialImporter : AssetPostprocessor
    {
        const string ModelsRoot = "Assets/ThirdParty/Models";

        // URP'nin kendi malzeme işleyicisinden sonra çalışsın
        public override int GetPostprocessOrder() => 100;

        static bool IsOurModel(string path) =>
            path.Replace('\\', '/').StartsWith(ModelsRoot);

        void OnPreprocessModel()
        {
            if (!IsOurModel(assetPath)) return;
            var importer = (ModelImporter)assetImporter;
            importer.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;
            importer.materialLocation = ModelImporterMaterialLocation.InPrefab;
        }

        void OnPreprocessMaterialDescription(MaterialDescription description, Material material,
            AnimationClip[] materialAnimation)
        {
            if (!IsOurModel(assetPath)) return;

            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) return;
            material.shader = shader;

            // Ana renk
            Color color = Color.white;
            if (description.TryGetProperty("DiffuseColor", out Vector4 diffuse))
                color = new Color(diffuse.x, diffuse.y, diffuse.z, 1f);
            else if (description.TryGetProperty("BaseColor", out Vector4 baseColor))
                color = new Color(baseColor.x, baseColor.y, baseColor.z, 1f);

            if (description.TryGetProperty("DiffuseFactor", out float factor) && factor > 0f && factor < 1f)
                color *= factor;

            material.SetColor("_BaseColor", color);
            material.color = color;

            // Yüzey: hafif mat
            material.SetFloat("_Metallic", 0f);
            material.SetFloat("_Smoothness", 0.25f);

            // Işık yayan parçalar (lamba, gösterge vb.)
            if (description.TryGetProperty("EmissiveColor", out Vector4 emissive))
            {
                float emissiveFactor = 1f;
                if (description.TryGetProperty("EmissiveFactor", out float ef)) emissiveFactor = ef;
                var e = new Color(emissive.x, emissive.y, emissive.z) * emissiveFactor;
                if (e.maxColorComponent > 0.01f)
                {
                    material.EnableKeyword("_EMISSION");
                    material.SetColor("_EmissionColor", e);
                    material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                }
            }
        }

        [MenuItem("MimicGame/Modelleri Yeniden İçe Aktar (renkler)")]
        public static void ReimportAll()
        {
            var guids = AssetDatabase.FindAssets("t:Model", new[] { ModelsRoot });
            try
            {
                AssetDatabase.StartAssetEditing();
                foreach (var guid in guids)
                    AssetDatabase.ImportAsset(AssetDatabase.GUIDToAssetPath(guid), ImportAssetOptions.ForceUpdate);
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }
            Debug.Log($"[Modeller] {guids.Length} model yeniden içe aktarıldı.");
        }
    }
}
