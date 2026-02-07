using Microsoft.UI.Xaml;

namespace TimeLapseCam
{
    public partial class App : Application
    {
        public App()
        {
            this.InitializeComponent();
        }

        protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            m_window = new MainWindow();
            m_window.Activate();

            // Initialize Analytics
            var analytics = new Services.FirebaseAnalyticsService();
            _ = analytics.LogEventAsync("app_launch");

            // Check EULA
            await CheckEulaAsync();
        }

        private async System.Threading.Tasks.Task CheckEulaAsync()
        {
            bool isAccepted = Services.SettingsHelper.Get<bool>("EulaAccepted", false);

            if (!isAccepted)
            {
                // Ensure XamlRoot is ready
                if (m_window.Content == null) return;
                
                var root = m_window.Content as FrameworkElement;
                if (root != null && root.XamlRoot != null)
                {
                    var dialog = new Views.EulaDialog();
                    dialog.XamlRoot = root.XamlRoot;
                    
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

        private Window? m_window;
    }
}
