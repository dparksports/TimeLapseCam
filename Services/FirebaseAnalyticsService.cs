using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.IO;
using TimeLapseCam.Services; // Ensure access to SettingsHelper

namespace TimeLapseCam.Services
{
    public class FirebaseAnalyticsService
    {
        private readonly HttpClient _httpClient = new HttpClient();
        private string? _measurementId;
        private string? _apiSecret;
        private string _clientId;

        public FirebaseAnalyticsService()
        {
            _clientId = GetOrCreateClientId();
            LoadConfiguration();
        }

        private void LoadConfiguration()
        {
            try 
            {
                var configFile = Path.Combine(AppContext.BaseDirectory, "Assets", "firebase_config.json");
                if (File.Exists(configFile))
                {
                    var json = File.ReadAllText(configFile);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    _measurementId = root.GetProperty("measurementId").GetString();
                    _apiSecret = root.GetProperty("apiSecret").GetString();
                }
            }
            catch (Exception)
            {
                // Silently fail if config is missing or invalid
            }
        }

        public async Task LogEventAsync(string eventName, object? parms = null)
        {
            // Check Opt-out status via SettingsHelper
            var enabled = SettingsHelper.Get<bool>("AnalyticsEnabled", true);
            if (!enabled) return;

            if (string.IsNullOrEmpty(_measurementId)) return;
            
            try
            {
                var url = $"https://www.google-analytics.com/mp/collect?measurement_id={_measurementId}&api_secret={_apiSecret}";
                var payload = new {
                    client_id = _clientId,
                    events = new[] { new { name = eventName, @params = parms ?? new { } } }
                };

                var json = JsonSerializer.Serialize(payload);
                System.Diagnostics.Debug.WriteLine($"[Analytics] Sending to {url}");
                System.Diagnostics.Debug.WriteLine($"[Analytics] Payload: {json}");
                
                var response = await _httpClient.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));
                
                if (!response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    System.Diagnostics.Debug.WriteLine($"[Analytics] Failed: {response.StatusCode} - {content}");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[Analytics] Success: {response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Analytics] Error: {ex}");
            }
        }

        private string GetOrCreateClientId()
        {
            var clientId = SettingsHelper.Get<string>("FirebaseClientId", string.Empty);
            if (string.IsNullOrEmpty(clientId))
            {
                clientId = Guid.NewGuid().ToString();
                SettingsHelper.Set("FirebaseClientId", clientId);
            }
            return clientId;
        }
    }
}
