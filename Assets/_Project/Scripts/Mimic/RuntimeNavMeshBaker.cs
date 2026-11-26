using Unity.AI.Navigation;
using UnityEngine;
using UnityEngine.AI;

namespace MimicGame.Mimic
{
    /// <summary>Sahne açılırken NavMesh'i çalışma anında üretir (baked asset gerekmez).</summary>
    [DefaultExecutionOrder(-100)]
    [RequireComponent(typeof(NavMeshSurface))]
    public class RuntimeNavMeshBaker : MonoBehaviour
    {
        public NavMeshSurface surface;

        void Awake()
        {
            if (surface == null) surface = GetComponent<NavMeshSurface>();
            if (surface == null) { Debug.LogError("[NavMesh] NavMeshSurface bulunamadı."); return; }
            surface.BuildNavMesh();
            int verts = NavMesh.CalculateTriangulation().vertices.Length;
            if (verts == 0) Debug.LogError("[NavMesh] Oluşturulan NavMesh boş (0 köşe)! Taklitçi gezinemez.");
            else Debug.Log("[NavMesh] Oluşturuldu: " + verts + " köşe");
        }
    }
}
