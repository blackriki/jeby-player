using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace EmbyPlayer.UI.Controls;

public enum PageStateKind
{
    Content,
    Loading,
    Empty,
    Error,
}

public partial class PageStateView : UserControl
{
    private static readonly DependencyPropertyKey CurrentStatePropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(CurrentState),
        typeof(PageStateKind),
        typeof(PageStateView),
        new PropertyMetadata(PageStateKind.Content));

    public static readonly DependencyProperty CurrentStateProperty = CurrentStatePropertyKey.DependencyProperty;

    public static readonly DependencyProperty IsLoadingProperty = RegisterStateProperty(nameof(IsLoading));
    public static readonly DependencyProperty IsEmptyProperty = RegisterStateProperty(nameof(IsEmpty));
    public static readonly DependencyProperty HasErrorProperty = RegisterStateProperty(nameof(HasError));
    public static readonly DependencyProperty LoadingMessageProperty = RegisterTextProperty(nameof(LoadingMessage), "正在加载...");
    public static readonly DependencyProperty EmptyTitleProperty = RegisterTextProperty(nameof(EmptyTitle), "暂无内容");
    public static readonly DependencyProperty EmptyMessageProperty = RegisterTextProperty(nameof(EmptyMessage), string.Empty);
    public static readonly DependencyProperty ErrorTitleProperty = RegisterTextProperty(nameof(ErrorTitle), "加载失败");
    public static readonly DependencyProperty ErrorMessageProperty = RegisterTextProperty(nameof(ErrorMessage), "暂时无法加载内容，请稍后重试。");
    public static readonly DependencyProperty ErrorHintProperty = RegisterTextProperty(nameof(ErrorHint), string.Empty);
    public static readonly DependencyProperty RetryButtonTextProperty = RegisterTextProperty(nameof(RetryButtonText), "重新加载");
    public static readonly DependencyProperty RetryToolTipProperty = RegisterTextProperty(nameof(RetryToolTip), "重新加载");
    public static readonly DependencyProperty RetryAutomationNameProperty = RegisterTextProperty(nameof(RetryAutomationName), "重新加载");
    public static readonly DependencyProperty ShowRetryProperty = DependencyProperty.Register(
        nameof(ShowRetry), typeof(bool), typeof(PageStateView), new PropertyMetadata(true));
    public static readonly DependencyProperty IsRetryEnabledProperty = DependencyProperty.Register(
        nameof(IsRetryEnabled), typeof(bool), typeof(PageStateView), new PropertyMetadata(true));
    public static readonly DependencyProperty RetryCommandProperty = DependencyProperty.Register(
        nameof(RetryCommand), typeof(ICommand), typeof(PageStateView));
    public static readonly DependencyProperty EmptyIconProperty = DependencyProperty.Register(
        nameof(EmptyIcon), typeof(Geometry), typeof(PageStateView));
    public static readonly DependencyProperty ErrorIconProperty = DependencyProperty.Register(
        nameof(ErrorIcon), typeof(Geometry), typeof(PageStateView));

    public PageStateView()
    {
        InitializeComponent();
    }

    public PageStateKind CurrentState => (PageStateKind)GetValue(CurrentStateProperty);

    public bool IsLoading { get => (bool)GetValue(IsLoadingProperty); set => SetValue(IsLoadingProperty, value); }
    public bool IsEmpty { get => (bool)GetValue(IsEmptyProperty); set => SetValue(IsEmptyProperty, value); }
    public bool HasError { get => (bool)GetValue(HasErrorProperty); set => SetValue(HasErrorProperty, value); }
    public bool ShowRetry { get => (bool)GetValue(ShowRetryProperty); set => SetValue(ShowRetryProperty, value); }
    public bool IsRetryEnabled { get => (bool)GetValue(IsRetryEnabledProperty); set => SetValue(IsRetryEnabledProperty, value); }
    public string LoadingMessage { get => (string)GetValue(LoadingMessageProperty); set => SetValue(LoadingMessageProperty, value); }
    public string EmptyTitle { get => (string)GetValue(EmptyTitleProperty); set => SetValue(EmptyTitleProperty, value); }
    public string EmptyMessage { get => (string)GetValue(EmptyMessageProperty); set => SetValue(EmptyMessageProperty, value); }
    public string ErrorTitle { get => (string)GetValue(ErrorTitleProperty); set => SetValue(ErrorTitleProperty, value); }
    public string ErrorMessage { get => (string)GetValue(ErrorMessageProperty); set => SetValue(ErrorMessageProperty, value); }
    public string ErrorHint { get => (string)GetValue(ErrorHintProperty); set => SetValue(ErrorHintProperty, value); }
    public string RetryButtonText { get => (string)GetValue(RetryButtonTextProperty); set => SetValue(RetryButtonTextProperty, value); }
    public string RetryToolTip { get => (string)GetValue(RetryToolTipProperty); set => SetValue(RetryToolTipProperty, value); }
    public string RetryAutomationName { get => (string)GetValue(RetryAutomationNameProperty); set => SetValue(RetryAutomationNameProperty, value); }
    public ICommand? RetryCommand { get => (ICommand?)GetValue(RetryCommandProperty); set => SetValue(RetryCommandProperty, value); }
    public Geometry? EmptyIcon { get => (Geometry?)GetValue(EmptyIconProperty); set => SetValue(EmptyIconProperty, value); }
    public Geometry? ErrorIcon { get => (Geometry?)GetValue(ErrorIconProperty); set => SetValue(ErrorIconProperty, value); }

    private static DependencyProperty RegisterStateProperty(string name)
    {
        return DependencyProperty.Register(
            name,
            typeof(bool),
            typeof(PageStateView),
            new PropertyMetadata(false, OnStatePropertyChanged));
    }

    private static DependencyProperty RegisterTextProperty(string name, string defaultValue)
    {
        return DependencyProperty.Register(name, typeof(string), typeof(PageStateView), new PropertyMetadata(defaultValue));
    }

    private static void OnStatePropertyChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        var view = (PageStateView)dependencyObject;
        view.SetValue(CurrentStatePropertyKey, ResolveState(view.IsLoading, view.IsEmpty, view.HasError));
    }

    public static PageStateKind ResolveState(bool isLoading, bool isEmpty, bool hasError)
    {
        return isLoading
            ? PageStateKind.Loading
            : hasError
                ? PageStateKind.Error
                : isEmpty
                    ? PageStateKind.Empty
                    : PageStateKind.Content;
    }
}
