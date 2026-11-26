using MimicGame.Player;

namespace MimicGame.Interaction
{
    /// <summary>E tuşuyla etkileşilebilen her şey (eşya, kapı, düğme...).</summary>
    public interface IInteractable
    {
        /// <summary>Ekranda gösterilecek yazı, ör. "Kapıyı aç".</summary>
        string GetPrompt(PlayerInteractor interactor);
        void Interact(PlayerInteractor interactor);
    }

    /// <summary>E basılı tutularak etkileşilen nesne (ör. şarj istasyonu).</summary>
    public interface IHoldInteractable : IInteractable
    {
        /// <summary>E basılıyken her karede çağrılır. Etkileşim sürüyorsa true.</summary>
        bool HoldUpdate(PlayerInteractor who, float dt);
        string GetHoldPrompt(PlayerInteractor who);
    }
}
