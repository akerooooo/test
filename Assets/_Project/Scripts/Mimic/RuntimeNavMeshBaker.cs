using Unity.AI.Navigation;
using UnityEngine;

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
            if (surface != null) surface.BuildNavMesh();
        }
    }
}
