using EmbyPlayer.Core.Home;

namespace EmbyPlayer.UI.ViewModels;

public sealed class HomeMediaSectionViewModel
{
    public HomeMediaSectionViewModel(HomeMediaSection section, HomeMediaSectionViewModel? currentSection)
    {
        Id = section.Id;
        Title = section.Title;
        IsLoadFailed = !section.IsSuccess;
        Items = section.IsSuccess
            ? section.Items.Select(item => new HomeMediaCardViewModel(item)).ToArray()
            : currentSection?.Items ?? Array.Empty<HomeMediaCardViewModel>();
    }

    public string Id { get; }

    public string Title { get; }

    public bool IsLoadFailed { get; }

    public IReadOnlyList<HomeMediaCardViewModel> Items { get; }

    public bool HasItems => Items.Count > 0;

    public bool IsEmpty => !IsLoadFailed && !HasItems;

    public bool IsVisible => true;
}
