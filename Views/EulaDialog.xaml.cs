using Microsoft.UI.Xaml.Controls;
using System;
using System.IO;

namespace TimeLapseCam.Views
{
    public sealed partial class EulaDialog : ContentDialog
    {
        public EulaDialog()
        {
            this.InitializeComponent();
            LoadTerms();
        }

        private void LoadTerms()
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "Assets", "terms.txt");
                TermsTextBlock.Text = File.Exists(path) ? File.ReadAllText(path) : "Terms not found.";
            }
            catch (Exception ex)
            {
                TermsTextBlock.Text = "Error loading terms: " + ex.Message;
            }
        }
    }
}
