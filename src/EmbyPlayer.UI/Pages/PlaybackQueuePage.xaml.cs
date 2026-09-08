using System.Windows;
using System.Windows.Controls;
using EmbyPlayer.UI.ViewModels;

namespace EmbyPlayer.UI.Pages;

public partial class PlaybackQueuePage : UserControl
{
    public static readonly DependencyProperty IsEmbeddedProperty = DependencyProperty.Register(
        nameof(IsEmbedded), typeof(bool), typeof(PlaybackQueuePage), new PropertyMetadata(false));

    public PlaybackQueuePage() => InitializeComponent();

    public bool IsEmbedded
    {
        get => (bool)GetValue(IsEmbeddedProperty);
        set => SetValue(IsEmbeddedProperty, value);
    }

    private void OnPositionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (sender is ComboBox { IsLoaded: true, DataContext: PlaybackQueueRowViewModel row, SelectedItem: int position }
            && position != row.Position && DataContext is PlaybackQueueViewModel viewModel)
            viewModel.MoveTo(row, position - 1);
    }
}
