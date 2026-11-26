using UnityEngine;

namespace MimicGame.Level
{
    /// <summary>Tesiste doğabilecek ganimet prefabları ve doğma ağırlıkları.</summary>
    [CreateAssetMenu(menuName = "MimicGame/Ganimet Tablosu", fileName = "LootTable")]
    public class LootTable : ScriptableObject
    {
        [System.Serializable]
        public struct Entry
        {
            public GameObject prefab;
            [Min(0f)] public float weight;
        }

        public Entry[] entries = new Entry[0];

        /// <summary>Ağırlığa göre rastgele prefab seçer (verilen rastgele üreteçle, deterministik).</summary>
        public GameObject Pick(System.Random rng)
        {
            if (entries == null || entries.Length == 0) return null;
            float total = 0f;
            for (int i = 0; i < entries.Length; i++)
                if (entries[i].prefab != null) total += Mathf.Max(0f, entries[i].weight);
            if (total <= 0f) return null;

            double r = rng.NextDouble() * total;
            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i].prefab == null) continue;
                r -= Mathf.Max(0f, entries[i].weight);
                if (r <= 0.0) return entries[i].prefab;
            }
            for (int i = entries.Length - 1; i >= 0; i--)
                if (entries[i].prefab != null) return entries[i].prefab;
            return null;
        }
    }
}
