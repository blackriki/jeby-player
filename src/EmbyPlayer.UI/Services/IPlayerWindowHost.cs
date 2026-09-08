namespace EmbyPlayer.UI.Services;

public interface IPlayerWindowHost : IPlayerCaptionHost
{
    void MinimizePlayerWindow();

    void ClosePlayerWindow();
}
