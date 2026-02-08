using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.IO;

namespace TimeLapseCam.Services
{
    public class FirebaseAnalyticsService
    {
        // Singleton
        private static FirebaseAnalyticsService? _instance;
        public static FirebaseAnalyticsService Instance => _instance ??= new FirebaseAnalyticsService();

        private readonly HttpClient _httpClient = new HttpClient();
        private string? _measurementId;
        private string? _apiSecret;
        private string? _firebaseAppId;
        private string _clientId;

        // Session management
        private long _sessionId;
        private int _sessionNumber;
        private bool _sessionStartSent;
        private const int SESSION_TIMEOUT_MINUTES = 30;

        private FirebaseAnalyticsService()
        {
            _clientId = GetOrCreateClientId();
            LoadConfiguration();
            _sessionId = GetOrCreateSessionId();
            _sessionNumber = GetOrIncrementSessionNumber(isNewSession: false);
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

                    if (root.TryGetProperty("appId", out var appIdEl))
                        _firebaseAppId = appIdEl.GetString();
                }
            }
            catch (Exception)
            {
                // Silently fail if config is missing or invalid
            }
        }

        /// <summary>
        /// Logs an event to GA4 via the Measurement Protocol.
        /// Automatically injects session_id, engagement_time_msec, and ga_session_number.
        /// Sends a session_start event before the first event in each new session.
        /// </summary>
        public async Task LogEventAsync(string eventName, object? parms = null)
        {
            // Check opt-out status
            var enabled = SettingsHelper.Get<bool>("AnalyticsEnabled", true);
            if (!enabled) return;

            if (string.IsNullOrEmpty(_measurementId) || string.IsNullOrEmpty(_apiSecret)) return;

            try
            {
                // Refresh session (regenerate if timed out)
                RefreshSession();

                // Send session_start if this is the first event of a new session
                if (!_sessionStartSent)
                {
                    _sessionStartSent = true;

                    // Only send session_start if the current event isn't already session_start
                    if (eventName != "session_start")
                    {
                        await SendEventAsync("session_start", BuildParams(null));
                    }
                }

                // Build merged params dictionary with session fields
                var mergedParams = BuildParams(parms);
                await SendEventAsync(eventName, mergedParams);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Analytics] Error: {ex}");
            }
        }

        /// <summary>
        /// Sends the actual HTTP request to GA4 Measurement Protocol endpoint.
        /// </summary>
        private async Task SendEventAsync(string eventName, Dictionary<string, object> eventParams)
        {
            var url = $"https://www.google-analytics.com/mp/collect?measurement_id={_measurementId}&api_secret={_apiSecret}";

            // Build the payload — include non_personalized_ads for privacy
            var payloadDict = new Dictionary<string, object>
            {
                ["client_id"] = _clientId,
                ["non_personalized_ads"] = true,
                ["events"] = new[] { new Dictionary<string, object>
                {
                    ["name"] = eventName,
                    ["params"] = eventParams
                }}
            };

            var json = JsonSerializer.Serialize(payloadDict);
            System.Diagnostics.Debug.WriteLine($"[Analytics] Sending: {eventName}");
            System.Diagnostics.Debug.WriteLine($"[Analytics] Payload: {json}");

            var response = await _httpClient.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));

            if (!response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                System.Diagnostics.Debug.WriteLine($"[Analytics] Failed: {response.StatusCode} - {content}");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[Analytics] Success: {response.StatusCode} [{eventName}]");
            }
        }

        /// <summary>
        /// Merges caller-supplied params with mandatory session fields.
        /// </summary>
        private Dictionary<string, object> BuildParams(object? callerParams)
        {
            var dict = new Dictionary<string, object>();

            // Flatten caller params into dictionary
            if (callerParams != null)
            {
                var json = JsonSerializer.Serialize(callerParams);
                var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
                if (parsed != null)
                {
                    foreach (var kv in parsed)
                    {
                        dict[kv.Key] = kv.Value;
                    }
                }
            }

            // Inject mandatory GA4 session fields (numeric values, not strings)
            dict["session_id"] = _sessionId.ToString();
            dict["engagement_time_msec"] = 100;
            dict["ga_session_number"] = _sessionNumber;

            return dict;
        }

        // ── Session management ──────────────────────────────────────────

        /// <summary>
        /// Checks whether the session has timed out (>30 min since last activity).
        /// If so, generates a new session ID. Always updates the last-activity timestamp.
        /// </summary>
        private void RefreshSession()
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var lastActivity = SettingsHelper.Get<long>("GA_LastActivity", 0);

            if (lastActivity > 0 && (now - lastActivity) > SESSION_TIMEOUT_MINUTES * 60)
            {
                // Session expired – start a new one
                _sessionId = now;
                SettingsHelper.Set("GA_SessionId", _sessionId);
                _sessionNumber = GetOrIncrementSessionNumber(isNewSession: true);
                _sessionStartSent = false; // Ensure session_start fires for new session
                System.Diagnostics.Debug.WriteLine($"[Analytics] Session expired, new session_id: {_sessionId}, session_number: {_sessionNumber}");
            }

            // Update last activity
            SettingsHelper.Set("GA_LastActivity", now);
        }

        /// <summary>
        /// Retrieves an existing session ID, or creates a new one if none exists
        /// or the previous session has timed out.
        /// </summary>
        private long GetOrCreateSessionId()
        {
            var lastActivity = SettingsHelper.Get<long>("GA_LastActivity", 0);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            if (lastActivity > 0 && (now - lastActivity) <= SESSION_TIMEOUT_MINUTES * 60)
            {
                // Resume existing session
                var existingId = SettingsHelper.Get<long>("GA_SessionId", 0);
                if (existingId > 0)
                {
                    _sessionStartSent = true; // Already sent in previous run
                    System.Diagnostics.Debug.WriteLine($"[Analytics] Resuming session_id: {existingId}");
                    return existingId;
                }
            }

            // No valid session – create new
            var newId = now;
            SettingsHelper.Set("GA_SessionId", newId);
            SettingsHelper.Set("GA_LastActivity", now);
            _sessionStartSent = false; // Will send session_start on first event
            System.Diagnostics.Debug.WriteLine($"[Analytics] New session_id: {newId}");
            return newId;
        }

        /// <summary>
        /// Tracks the total number of sessions for this user.
        /// Increments on new session, returns current count otherwise.
        /// </summary>
        private int GetOrIncrementSessionNumber(bool isNewSession)
        {
            var current = SettingsHelper.Get<int>("GA_SessionNumber", 0);
            if (isNewSession || current == 0)
            {
                current++;
                SettingsHelper.Set("GA_SessionNumber", current);
            }
            return current;
        }

        // ── Client ID persistence ───────────────────────────────────────

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
