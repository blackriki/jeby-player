using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Images;
using EmbyPlayer.UI.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

[TestClass]
public sealed class AuthenticatedImageTests
{
    [TestMethod]
    public void AuthenticatedImage_CacheClearRefreshesLoadedBitmapAndInvalidatesDetachedBitmap()
    {
        RunOnStaDispatcherThread(async () =>
        {
            var service = new InvalidatingImageService();
            ConfigureImageServices(service);
            var control = new AuthenticatedImage { ImageUrl = "https://images.example.test/items/cache" };
            var window = new Window
            {
                Content = control, Width = 200, Height = 200, Left = -10000, Top = -10000,
                ShowActivated = false, ShowInTaskbar = false, Opacity = 0
            };
            try
            {
                window.Show();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var image = (Image)control.FindName("ImageElement");
                Assert.IsTrue(control.IsImageLoaded);
                var firstBitmap = image.Source;
                var firstCalls = service.CallCount;
                service.Clear();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.IsTrue(control.IsImageLoaded);
                Assert.AreNotSame(firstBitmap, image.Source);
                Assert.AreEqual(firstCalls + 1, service.CallCount);

                window.Content = null;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var detachedBitmap = image.Source;
                service.Clear();
                window.Content = control;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.AreNotSame(detachedBitmap, image.Source);
                Assert.AreEqual(firstCalls + 2, service.CallCount);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void AuthenticatedImage_UsesPlaceholderWhenImageBytesCannotDecode()
    {
        RunOnStaDispatcherThread(async () =>
        {
            ConfigureImageServices(new ImmediateImageService(
                ImageLoadResult.Success(new byte[] { 0x00, 0x01, 0x02, 0x03 }, "image/jpeg")));
            var control = new AuthenticatedImage
            {
                ImageUrl = "https://images.example.test/items/corrupt",
            };

            await InvokeLoadImageAsync(control);

            AssertPlaceholderIsVisible(control);
            Assert.IsFalse(control.IsImageLoaded);
        });
    }

    [TestMethod]
    public void AuthenticatedImage_CanDisablePosterPlaceholderForExternalFallback()
    {
        RunOnStaDispatcherThread(async () =>
        {
            ConfigureImageServices(new ImmediateImageService(
                ImageLoadResult.Failure(ImageLoadError.NotFound)));
            var control = new AuthenticatedImage
            {
                ImageUrl = "https://images.example.test/items/missing-logo",
                ShowPlaceholder = false,
            };

            await InvokeLoadImageAsync(control);

            var placeholder = control.FindName("PlaceholderBorder") as Border;
            var root = control.FindName("RootGrid") as Grid;
            Assert.IsNotNull(placeholder);
            Assert.IsNotNull(root);
            Assert.AreEqual(Visibility.Collapsed, placeholder.Visibility);
            Assert.AreEqual(Colors.Transparent, ((SolidColorBrush)root.Background).Color);
            Assert.IsFalse(control.IsImageLoaded);
        });
    }

    [TestMethod]
    public void AuthenticatedImage_DefaultPlaceholderKeepsOriginalDarkBackground()
    {
        RunOnStaDispatcherThread(() =>
        {
            var control = new AuthenticatedImage();
            var root = control.FindName("RootGrid") as Grid;

            Assert.IsNotNull(root);
            Assert.IsTrue(control.ShowPlaceholder);
            Assert.AreEqual(Color.FromRgb(0x15, 0x1A, 0x20), ((SolidColorBrush)root.Background).Color);
            return Task.CompletedTask;
        });
    }

    [TestMethod]
    public void AuthenticatedImage_ImageContentAlignmentDefaultsToCenterAndCanAlignRight()
    {
        RunOnStaDispatcherThread(async () =>
        {
            var control = new AuthenticatedImage();
            control.ApplyTemplate();
            await control.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
            var image = control.FindName("ImageElement") as Image;

            Assert.IsNotNull(image);
            Assert.AreEqual(HorizontalAlignment.Center, control.ImageHorizontalAlignment);
            Assert.AreEqual(HorizontalAlignment.Center, image.HorizontalAlignment);

            control.ImageHorizontalAlignment = HorizontalAlignment.Right;
            await control.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);

            Assert.AreEqual(HorizontalAlignment.Right, image.HorizontalAlignment);
        });
    }

    [TestMethod]
    public void AuthenticatedImage_SuccessExposesLoadedState()
    {
        RunOnStaDispatcherThread(async () =>
        {
            ConfigureImageServices(new ImmediateImageService(
                ImageLoadResult.Success(ValidPngBytes, "image/png")));
            var control = new AuthenticatedImage
            {
                ImageUrl = "https://images.example.test/items/logo",
                ShowPlaceholder = false,
            };

            await InvokeLoadImageAsync(control);

            var image = control.FindName("ImageElement") as Image;
            Assert.IsNotNull(image?.Source);
            Assert.IsTrue(control.IsImageLoaded);
        });
    }

    [TestMethod]
    public void AuthenticatedImage_ReloadsSameSourceAfterUnloadedCancelsRequest()
    {
        RunOnStaDispatcherThread(async () =>
        {
            var imageService = new SequencedImageService();
            ConfigureImageServices(imageService);
            var control = new AuthenticatedImage
            {
                ImageUrl = "https://images.example.test/items/replayed-logo",
                ShowPlaceholder = false,
            };

            control.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Assert.AreEqual(1, imageService.Requests.Count);

            control.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            Assert.IsTrue(imageService.Requests[0].CancellationToken.IsCancellationRequested);

            control.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Assert.AreEqual(2, imageService.Requests.Count);
            imageService.Requests[1].Complete(ImageLoadResult.Success(ValidPngBytes, "image/png"));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

            var image = control.FindName("ImageElement") as Image;
            Assert.IsNotNull(image?.Source);
            Assert.IsTrue(control.IsImageLoaded);
        });
    }

    [TestMethod]
    public void AuthenticatedImage_ReloadedWithCurrentImageDoesNotRequestAgain()
    {
        RunOnStaDispatcherThread(async () =>
        {
            var imageService = new SequencedImageService();
            ConfigureImageServices(imageService);
            var control = new AuthenticatedImage
            {
                ImageUrl = "https://images.example.test/items/cached-logo",
                ShowPlaceholder = false,
            };

            control.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            imageService.Requests[0].Complete(ImageLoadResult.Success(ValidPngBytes, "image/png"));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            control.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            control.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));

            Assert.AreEqual(1, imageService.Requests.Count);
            Assert.IsTrue(control.IsImageLoaded);
        });
    }

    [TestMethod]
    public void AuthenticatedImage_OldRequestCannotReplaceNewPlaceholder()
    {
        RunOnStaDispatcherThread(async () =>
        {
            var imageService = new DeferredImageService();
            ConfigureImageServices(imageService);
            var control = new AuthenticatedImage
            {
                ImageUrl = "https://images.example.test/items/old",
            };

            var oldRequest = InvokeLoadImageAsync(control);
            await imageService.WaitForRequestAsync("https://images.example.test/items/old");

            control.ImageUrl = "https://images.example.test/items/new";
            var newRequest = InvokeLoadImageAsync(control);
            await imageService.WaitForRequestAsync("https://images.example.test/items/new");

            imageService.Complete(
                "https://images.example.test/items/new",
                ImageLoadResult.Failure(ImageLoadError.NotFound));
            await newRequest;

            imageService.Complete(
                "https://images.example.test/items/old",
                ImageLoadResult.Success(ValidPngBytes, "image/png"));
            await oldRequest;

            AssertPlaceholderIsVisible(control);
        });
    }

    [TestMethod]
    public void AuthenticatedImage_OldRequestCannotReplaceSuccessfulNewSource()
    {
        RunOnStaDispatcherThread(async () =>
        {
            var imageService = new DeferredImageService();
            ConfigureImageServices(imageService);
            var control = new AuthenticatedImage
            {
                ImageUrl = "https://images.example.test/items/old-success",
            };

            var oldRequest = InvokeLoadImageAsync(control);
            await imageService.WaitForRequestAsync("https://images.example.test/items/old-success");
            control.ImageUrl = "https://images.example.test/items/new-success";
            var newRequest = InvokeLoadImageAsync(control);
            await imageService.WaitForRequestAsync("https://images.example.test/items/new-success");

            imageService.Complete(
                "https://images.example.test/items/new-success",
                ImageLoadResult.Success(ValidPngBytes, "image/png"));
            await newRequest;
            var image = control.FindName("ImageElement") as Image;
            var newSource = image?.Source;

            imageService.Complete(
                "https://images.example.test/items/old-success",
                ImageLoadResult.Success(ValidPngBytes, "image/png"));
            await oldRequest;

            Assert.IsNotNull(newSource);
            Assert.AreSame(newSource, image?.Source);
            Assert.IsTrue(control.IsImageLoaded);
        });
    }

    private static readonly byte[] ValidPngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAF/gL+taBUOwAAAABJRU5ErkJggg==");

    private static void ConfigureImageServices(IImageService imageService)
    {
        var currentSessionService = new CurrentSessionService();
        currentSessionService.SetSession(new AuthSession(
            "https://server.example.test",
            "test-token",
            "user-1",
            "Test User",
            "server-1"));
        ImageLoaderServices.Configure(imageService, currentSessionService);
    }

    private static Task InvokeLoadImageAsync(AuthenticatedImage control)
    {
        var method = typeof(AuthenticatedImage).GetMethod(
            "LoadImageAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method);

        var loadTask = method.Invoke(control, null) as Task;
        Assert.IsNotNull(loadTask);
        return loadTask;
    }

    private static void AssertPlaceholderIsVisible(AuthenticatedImage control)
    {
        var placeholder = control.FindName("PlaceholderTextBlock") as TextBlock;
        var image = control.FindName("ImageElement") as Image;

        Assert.IsNotNull(placeholder);
        Assert.IsNotNull(image);
        Assert.AreEqual(Visibility.Visible, placeholder.Visibility);
        Assert.IsNull(image.Source);
    }

    private static void RunOnStaDispatcherThread(Func<Task> action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                EnsureApplicationResources();
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                PumpDispatcherUntilCompleted(action());
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (exception is not null)
        {
            throw exception;
        }
    }

    private static void PumpDispatcherUntilCompleted(Task task)
    {
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(
            _ => frame.Continue = false,
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.FromCurrentSynchronizationContext());
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    private static void EnsureApplicationResources()
    {
        if (Application.Current is null)
        {
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        }

        Application.Current!.Resources["TextSecondaryBrush"] = Brushes.LightGray;
    }

    private sealed class InvalidatingImageService : IImageService, IImageCacheInvalidationSource
    {
        public event EventHandler? CacheCleared;
        public int CallCount { get; private set; }
        public void Clear() => CacheCleared?.Invoke(this, EventArgs.Empty);
        public Task<ImageLoadResult> LoadImageAsync(AuthSession session, string imageUrl, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(ImageLoadResult.Success(ValidPngBytes, "image/png"));
        }
    }

    private sealed class ImmediateImageService : IImageService
    {
        private readonly ImageLoadResult result;

        public ImmediateImageService(ImageLoadResult result)
        {
            this.result = result;
        }

        public Task<ImageLoadResult> LoadImageAsync(
            AuthSession session,
            string imageUrl,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(result);
        }
    }

    private sealed class DeferredImageService : IImageService
    {
        private readonly Dictionary<string, TaskCompletionSource<ImageLoadResult>> requests = new();
        private readonly Dictionary<string, TaskCompletionSource<object?>> requestStarted = new();

        public Task<ImageLoadResult> LoadImageAsync(
            AuthSession session,
            string imageUrl,
            CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<ImageLoadResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            requests.Add(imageUrl, completion);

            var started = new TaskCompletionSource<object?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            requestStarted.Add(imageUrl, started);
            started.SetResult(null);
            return completion.Task;
        }

        public Task WaitForRequestAsync(string imageUrl)
        {
            return requestStarted[imageUrl].Task;
        }

        public void Complete(string imageUrl, ImageLoadResult result)
        {
            requests[imageUrl].SetResult(result);
        }
    }

    private sealed class SequencedImageService : IImageService
    {
        public List<Request> Requests { get; } = [];

        public Task<ImageLoadResult> LoadImageAsync(
            AuthSession session,
            string imageUrl,
            CancellationToken cancellationToken)
        {
            var request = new Request(imageUrl, cancellationToken);
            Requests.Add(request);
            cancellationToken.Register(() => request.Completion.TrySetCanceled(cancellationToken));
            return request.Completion.Task;
        }

        public sealed class Request
        {
            public Request(string imageUrl, CancellationToken cancellationToken)
            {
                ImageUrl = imageUrl;
                CancellationToken = cancellationToken;
            }

            public string ImageUrl { get; }

            public CancellationToken CancellationToken { get; }

            public TaskCompletionSource<ImageLoadResult> Completion { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public void Complete(ImageLoadResult result) => Completion.TrySetResult(result);
        }
    }
}
