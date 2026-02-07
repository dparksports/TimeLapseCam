using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TimeLapseCam.Services;

namespace TimeLapseCam.Views
{
    public sealed partial class SettingsPage : Page
    {
        public SettingsPage()
        {
            this.InitializeComponent();
            LoadSettings();
        }

        private void LoadSettings()
        {
            // Load initial state without triggering event if possible, 
            // but ToggleSwitch event fires on assignment if changed.
            // We use a flag or just handle it safe.
            bool enabled = SettingsHelper.Get<bool>("AnalyticsEnabled", true);
            AnalyticsToggle.IsOn = enabled;
        }

        private void AnalyticsToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch toggle)
            {
                SettingsHelper.Set("AnalyticsEnabled", toggle.IsOn);
            }
        }
    }
}
