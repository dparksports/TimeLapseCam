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
