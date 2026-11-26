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
}
