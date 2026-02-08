using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace TimeLapseCam
{
    public partial class App : Application
    {
        public App()
        {
            this.InitializeComponent();
        }

        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            m_window = new MainWindow();
            m_window.Activate();

            // Restore saved window size or apply default 50% height increase
            var appWindow = m_window.AppWindow;
            int savedW = Services.SettingsHelper.Get<int>("WindowWidth", 0);
            int savedH = Services.SettingsHelper.Get<int>("WindowHeight", 0);

            if (savedW > 100 && savedH > 100)
            {
                appWindow.Resize(new SizeInt32(savedW, savedH));
            }
            else
            {
                var size = appWindow.Size;
                appWindow.Resize(new SizeInt32(size.Width, (int)(size.Height * 1.5)));
            }

            // Save window size on close
            m_window.Closed += (s, e) =>
            {
                var finalSize = appWindow.Size;
                Services.SettingsHelper.Set("WindowWidth", finalSize.Width);
                Services.SettingsHelper.Set("WindowHeight", finalSize.Height);
            };

            // Execute post-launch logic after window is visible
            m_window.DispatcherQueue.TryEnqueue(async () =>
            {
                // Wait for XamlRoot to be ready
                await Task.Delay(100);
                
                // Check EULA first (blocking)
                await CheckEulaAsync();
                
                // Initialize Analytics after EULA
                _ = Services.FirebaseAnalyticsService.Instance.LogEventAsync("app_launch");
            });
        }

        private async System.Threading.Tasks.Task CheckEulaAsync()
        {
            bool isAccepted = Services.SettingsHelper.Get<bool>("EulaAccepted", false);

            if (!isAccepted)
            {
                // Ensure XamlRoot is ready
                if (m_window?.Content == null) return;
                
                var root = m_window.Content as FrameworkElement;
                if (root != null && root.XamlRoot != null)
                {
                    var dialog = new Views.EulaDialog();
                    dialog.XamlRoot = root.XamlRoot!;
                    
                    var result = await dialog.ShowAsync();
                    if (result == ContentDialogResult.Primary)
                    {
                        Services.SettingsHelper.Set("EulaAccepted", true);
                    }
                    else
                    {
                        // Exit
                        Application.Current.Exit();
                    }
                }
            }
        }

        public static Window? MainWindow => ((App)Current).m_window;

        private Window? m_window;
    }
}
