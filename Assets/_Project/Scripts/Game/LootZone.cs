using MimicGame.Items;
using UnityEngine;

namespace MimicGame.Game
{
    /// <summary>
    /// Aracın kasası. İçine bırakılan (elde tutulmayan) eşyaların toplam değerini sayar.
    /// Bir BoxCollider (Is Trigger) ile alanı belirlenir.
    /// </summary>
    [RequireComponent(typeof(BoxCollider))]
    public class LootZone : MonoBehaviour
    {
        public int quota = 150;

        public static LootZone Main { get; private set; }
        public int CollectedValue { get; private set; }
        public int CollectedCount { get; private set; }

        BoxCollider area;

        void Awake()
        {
            area = GetComponent<BoxCollider>();
            area.isTrigger = true;
        }

        void OnEnable() => Main = this;
        void OnDisable() { if (Main == this) Main = null; }

        void Update()
        {
            int total = 0, count = 0;
            var bounds = area.bounds;
            foreach (var item in Item.All)
            {
                if (item.IsHeld || !bounds.Contains(item.transform.position)) continue;
                total += item.value;
                count++;
            }
            CollectedValue = total;
            CollectedCount = count;
        }
    }
}
