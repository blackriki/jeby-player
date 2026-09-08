namespace EmbyPlayer.Player;

internal interface IMpvNativeApi
{
    IntPtr Create();

    int Initialize(IntPtr context);

    int RequestLogMessages(IntPtr context, string minimumLevel);

    int SetOptionString(IntPtr context, string name, string value);

    int SetPropertyString(IntPtr context, string name, string value);

    int Command(IntPtr context, IReadOnlyList<string> args);

    MpvEventSnapshot WaitEvent(IntPtr context, double timeout);

    string? GetPropertyString(IntPtr context, string name);

    bool? GetFlagProperty(IntPtr context, string name);

    double? GetDoubleProperty(IntPtr context, string name);

    void Wakeup(IntPtr context);

    void TerminateDestroy(IntPtr context);
}
