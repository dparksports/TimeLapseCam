using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TimeLapseCam.Views;
using System;
using System.Linq;

namespace TimeLapseCam
{
    public sealed partial class MainWindow : Window
    {
        public MainWindow()
        {
            this.InitializeComponent();
            this.Title = "TimeLapse Webcam Recorder";
            
            // Set window to compact size (~50% of default)
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
            appWindow.Resize(new Windows.Graphics.SizeInt32(1040, 500));
            
            // Set initial page
            NavView.SelectedItem = NavView.MenuItems.OfType<NavigationViewItem>().First();
            ContentFrame.Navigate(typeof(CapturePage));
        }

        private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (args.IsSettingsSelected)
            {
                ContentFrame.Navigate(typeof(SettingsPage));
            }
            else if (args.SelectedItemContainer is NavigationViewItem selectedItem && selectedItem.Tag is string tag)
            {
                switch (tag)
                {
                    case "CapturePage":
                        ContentFrame.Navigate(typeof(CapturePage));
                        break;
                    case "ReviewPage":
                        ContentFrame.Navigate(typeof(ReviewPage));
                        break;
                }
            }
        }
    }
}
