using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TimeLapseCam.Services;

namespace TimeLapseCam.Views
{
    public sealed partial class SettingsPage : Page
    {
        private readonly TranscriptionService _transcriptionService = new();
        private bool _isLoading = true;

        public SettingsPage()
        {
            this.InitializeComponent();
            LoadSettings();
            _isLoading = false;
            _ = CheckFfmpegStatusAsync();
            _ = CheckVcRuntimeStatusAsync();
        }

        private void LoadSettings()
        {
            // Analytics
            bool enabled = SettingsHelper.Get<bool>("AnalyticsEnabled", true);
            AnalyticsToggle.IsOn = enabled;

            // Preview
            bool preview = SettingsHelper.Get<bool>("PreviewEnabled", true);
            PreviewToggle.IsOn = preview;

            // Delay Start
            int delay = SettingsHelper.Get<int>("DelayStartMinutes", 10);
            DelaySlider.Value = delay;
            UpdateDelayText(delay);

            // Debug Log
            bool debugLog = SettingsHelper.Get<bool>("ShowTranscriptDebugLog", false);
            DebugLogToggle.IsOn = debugLog;

            // Whisper model
            string model = SettingsHelper.Get<string>("WhisperModel", "base") ?? "base";
            foreach (ComboBoxItem item in WhisperModelCombo.Items)
            {
                if (item.Tag?.ToString() == model)
                {
                    WhisperModelCombo.SelectedItem = item;
                    break;
                }
            }
        }

        private void UpdateDelayText(int minutes)
        {
            DelayValueText.Text = minutes == 0 ? "Instant" : $"{minutes} min";
        }

        private async System.Threading.Tasks.Task CheckFfmpegStatusAsync()
        {
            FfmpegStatusText.Text = "⏳ Checking...";
            FfmpegInstallButton.IsEnabled = false;

            bool found = await _transcriptionService.EnsureFfmpegAsync();

            if (found)
            {
                string path = _transcriptionService.GetFfmpegPath();
                string shortPath = path.Length > 60 ? "..." + path.Substring(path.Length - 55) : path;
                FfmpegStatusText.Text = $"✅ {shortPath}";
                FfmpegInstallButton.Content = "🔄 Reinstall";
            }
            else
            {
                FfmpegStatusText.Text = "❌ Not installed";
                FfmpegInstallButton.Content = "⬇ Install ffmpeg";
            }
            FfmpegInstallButton.IsEnabled = true;
        }

        private async void FfmpegInstall_Click(object sender, RoutedEventArgs e)
        {
            FfmpegInstallButton.IsEnabled = false;
            FfmpegInstallButton.Content = "⏳ Installing...";
            FfmpegStatusText.Text = "Installing ffmpeg via winget — this may take a minute...";

            _transcriptionService.ProgressChanged += OnFfmpegProgress;

            bool success = await _transcriptionService.InstallFfmpegAsync();

            _transcriptionService.ProgressChanged -= OnFfmpegProgress;

            if (success)
            {
                string path = _transcriptionService.GetFfmpegPath();
                string shortPath = path.Length > 60 ? "..." + path.Substring(path.Length - 55) : path;
                FfmpegStatusText.Text = $"✅ {shortPath}";
                FfmpegInstallButton.Content = "🔄 Reinstall";
            }
            else
            {
                FfmpegStatusText.Text = "❌ Install failed. Run in terminal: winget install Gyan.FFmpeg";
                FfmpegInstallButton.Content = "⬇ Retry Install";
            }
            FfmpegInstallButton.IsEnabled = true;
        }

        private void OnFfmpegProgress(object? sender, string message)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                FfmpegStatusText.Text = message;
            });
        }

        // --- VC++ Runtime ---

        private static bool IsVcRuntimeInstalled()
        {
            string vcPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "vcruntime140.dll");
            return System.IO.File.Exists(vcPath);
        }

        private async System.Threading.Tasks.Task CheckVcRuntimeStatusAsync()
        {
            VcRuntimeStatusText.Text = "⏳ Checking...";
            VcRuntimeInstallButton.IsEnabled = false;

            bool installed = await System.Threading.Tasks.Task.Run(() => IsVcRuntimeInstalled());

            if (installed)
            {
                VcRuntimeStatusText.Text = "✅ VC++ Runtime installed";
                VcRuntimeInstallButton.Content = "🔄 Reinstall";
            }
            else
            {
                VcRuntimeStatusText.Text = "❌ Not installed — required for GPU transcription";
                VcRuntimeInstallButton.Content = "⬇ Install VC++ Runtime";
            }
            VcRuntimeInstallButton.IsEnabled = true;
        }

        private async void VcRuntimeInstall_Click(object sender, RoutedEventArgs e)
        {
            VcRuntimeInstallButton.IsEnabled = false;
            VcRuntimeInstallButton.Content = "⏳ Installing...";
            VcRuntimeStatusText.Text = "Downloading VC++ 2015-2022 x64 from Microsoft...";

            try
            {
                // Download from official Microsoft URL
                string url = "https://aka.ms/vs/17/release/vc_redist.x64.exe";
                string tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vc_redist.x64.exe");

                using var httpClient = new System.Net.Http.HttpClient();
                using var response = await httpClient.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                long? totalBytes = response.Content.Headers.ContentLength;
                using var contentStream = await response.Content.ReadAsStreamAsync();
                using var fileStream = System.IO.File.Create(tempPath);

                var buffer = new byte[81920];
                long downloaded = 0;
                int bytesRead;
                while ((bytesRead = await contentStream.ReadAsync(buffer)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                    downloaded += bytesRead;
                    if (totalBytes.HasValue)
                    {
                        int pct = (int)(downloaded * 100 / totalBytes.Value);
                        DispatcherQueue.TryEnqueue(() =>
                            VcRuntimeStatusText.Text = $"Downloading... {pct}% ({downloaded / 1024 / 1024}MB / {totalBytes.Value / 1024 / 1024}MB)");
                    }
                }
                fileStream.Close();

                DispatcherQueue.TryEnqueue(() =>
                    VcRuntimeStatusText.Text = "Installing VC++ Runtime (may require admin)...");

                // Run the installer quietly
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = tempPath,
                    Arguments = "/install /quiet /norestart",
                    UseShellExecute = true,  // UseShellExecute for elevation
                    Verb = "runas"
                };

                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc != null)
                {
                    await proc.WaitForExitAsync();
                }

                // Clean up temp file
                try { System.IO.File.Delete(tempPath); } catch { }

                // Check result
                bool installed = IsVcRuntimeInstalled();
                if (installed)
                {
                    VcRuntimeStatusText.Text = "✅ VC++ Runtime installed — restart app to use GPU transcription";
                    VcRuntimeInstallButton.Content = "🔄 Reinstall";
                }
                else
                {
                    VcRuntimeStatusText.Text = "❌ Install may have failed. Try: winget install Microsoft.VCRedist.2015+.x64";
                    VcRuntimeInstallButton.Content = "⬇ Retry Install";
                }
            }
            catch (Exception ex)
            {
                VcRuntimeStatusText.Text = $"❌ Error: {ex.Message}";
                VcRuntimeInstallButton.Content = "⬇ Retry Install";
            }
            VcRuntimeInstallButton.IsEnabled = true;
        }

        private void PreviewToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            if (sender is ToggleSwitch toggle)
            {
                SettingsHelper.Set("PreviewEnabled", toggle.IsOn);
            }
        }

        private void DelaySlider_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_isLoading) return;
            int val = (int)e.NewValue;
            SettingsHelper.Set("DelayStartMinutes", val);
            UpdateDelayText(val);
        }

        private void AnalyticsToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            if (sender is ToggleSwitch toggle)
            {
                SettingsHelper.Set("AnalyticsEnabled", toggle.IsOn);
            }
        }

        private void DebugLogToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            if (sender is ToggleSwitch toggle)
            {
                SettingsHelper.Set("ShowTranscriptDebugLog", toggle.IsOn);
            }
        }

        private void WhisperModel_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;
            if (WhisperModelCombo.SelectedItem is ComboBoxItem selected)
            {
                string model = selected.Tag?.ToString() ?? "base";
                SettingsHelper.Set("WhisperModel", model);
            }
        }
    }
}
