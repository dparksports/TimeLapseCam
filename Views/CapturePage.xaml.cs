using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using TimeLapseCam.ViewModels;

namespace TimeLapseCam.Views
{
    public sealed partial class CapturePage : Page
    {
        public MainViewModel ViewModel { get; }

        private static readonly SolidColorBrush ArmedBrush = new(Colors.OrangeRed);
        private static readonly SolidColorBrush RecordingBrush = new(Colors.Red);
        private static readonly SolidColorBrush DefaultBrush = new(Colors.Transparent);
        private static readonly SolidColorBrush ArmedForeground = new(Colors.White);

        public CapturePage()
        {
            this.InitializeComponent();
            ViewModel = new MainViewModel();

            this.Loaded += CapturePage_Loaded;
            this.Unloaded += CapturePage_Unloaded;

            ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        }

        private void CapturePage_Loaded(object sender, RoutedEventArgs e)
        {
            _ = ViewModel.InitializeAsync();
            // Re-read preview setting in case user toggled it in Settings
            ViewModel.IsPreviewEnabled = Services.SettingsHelper.Get<bool>("PreviewEnabled", true);
            UpdatePreviewVisibility();
            UpdateButtonAppearance();
        }

        private void CapturePage_Unloaded(object sender, RoutedEventArgs e)
        {
            // ViewModel.Cleanup();
        }

        private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName is "IsArmed" or "IsRecording" or "IsPreviewEnabled")
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (e.PropertyName == "IsPreviewEnabled")
                        UpdatePreviewVisibility();
                    else
                        UpdateButtonAppearance();
                });
            }
        }

        private void UpdateButtonAppearance()
        {
            if (ViewModel.IsArmed)
            {
                RecordButton.Background = ArmedBrush;
                RecordButton.Foreground = ArmedForeground;
            }
            else if (ViewModel.IsRecording)
            {
                RecordButton.Background = RecordingBrush;
                RecordButton.Foreground = ArmedForeground;
            }
            else
            {
                RecordButton.Background = DefaultBrush;
                RecordButton.ClearValue(Button.ForegroundProperty);
            }
        }

        private void UpdatePreviewVisibility()
        {
            PreviewImage.Visibility = ViewModel.IsPreviewEnabled
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }
}
