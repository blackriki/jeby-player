namespace EmbyPlayer.UI.Navigation;

public sealed record ServerConnectionNavigationParameter(string Message)
{
    public const string SwitchServerPartialFailureMessage =
        "登录状态已清除，但无法清除已保存的服务器地址。请确认或修改地址后重新连接。";

    public const string AuthenticationCleanupPartialFailureMessage =
        "安全令牌已清除，但本地账户信息未能完全清理。请确认服务器地址后重新连接。";
}
