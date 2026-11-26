using UnityEngine;

namespace MimicGame.Items
{
    /// <summary>
    /// Gerçek 3B model gelince ilkel şeklin yerine geçmesi için kanca. Builder (editör)
    /// Assets/ThirdParty/Models altında {modelName}.fbx arar; bulursa görseli onunla değiştirir.
    /// </summary>
    [DisallowMultipleComponent]
    public class ModelSwap : MonoBehaviour
    {
        [Tooltip("ASCII snake_case model adı, ör. eski_saat")]
        public string modelName;
        [Tooltip("Builder gerçek modeli bulup kullandıysa true.")]
        public bool hasModel;
    }
}
