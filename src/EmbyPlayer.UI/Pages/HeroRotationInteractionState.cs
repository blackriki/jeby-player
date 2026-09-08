namespace EmbyPlayer.UI.Pages;

public enum HeroRotationDirectiveKind
{
    None,
    Stop,
    Schedule
}

public readonly record struct HeroRotationDirective(
    HeroRotationDirectiveKind Kind,
    TimeSpan Delay)
{
    public static HeroRotationDirective None => new(HeroRotationDirectiveKind.None, TimeSpan.Zero);

    public static HeroRotationDirective Stop => new(HeroRotationDirectiveKind.Stop, TimeSpan.Zero);

    public static HeroRotationDirective Schedule(TimeSpan delay) =>
        new(HeroRotationDirectiveKind.Schedule, delay);
}

public sealed class HeroRotationInteractionState
{
    public static readonly TimeSpan NormalDelay = TimeSpan.FromSeconds(7);
    public static readonly TimeSpan PointerLeaveDelay = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan ManualResumeDelay = TimeSpan.FromSeconds(6);

    public bool HasKeyboardInteraction { get; private set; }

    public bool HasManualPointerInteraction { get; private set; }

    public HeroRotationDirective OnPointerEntered() => HeroRotationDirective.Stop;

    public HeroRotationDirective OnPointerManualInteraction()
    {
        HasManualPointerInteraction = true;
        HasKeyboardInteraction = false;
        return HeroRotationDirective.Stop;
    }

    public HeroRotationDirective OnPointerExited()
    {
        var delay = HasManualPointerInteraction ? ManualResumeDelay : PointerLeaveDelay;
        HasManualPointerInteraction = false;
        return HeroRotationDirective.Schedule(delay);
    }

    public HeroRotationDirective OnKeyboardInteraction()
    {
        HasKeyboardInteraction = true;
        return HeroRotationDirective.Stop;
    }

    public HeroRotationDirective OnKeyboardFocusLeft()
    {
        if (!HasKeyboardInteraction)
        {
            return HeroRotationDirective.None;
        }

        HasKeyboardInteraction = false;
        return HeroRotationDirective.Schedule(ManualResumeDelay);
    }

    public HeroRotationDirective OnWindowDeactivated() => HeroRotationDirective.Stop;

    public HeroRotationDirective OnWindowActivated() =>
        HeroRotationDirective.Schedule(NormalDelay);

    public HeroRotationDirective OnUnloaded()
    {
        Reset();
        return HeroRotationDirective.Stop;
    }

    public void Reset()
    {
        HasKeyboardInteraction = false;
        HasManualPointerInteraction = false;
    }
}
