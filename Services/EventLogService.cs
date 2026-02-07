using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace TimeLapseCam.Services
{
    public class EventLogService
    {
        private string _logFilePath = string.Empty;
        private readonly List<EventItem> _events = new();
        private readonly object _lock = new();

        public void Initialize(string videoFilePath)
        {
            _logFilePath = Path.ChangeExtension(videoFilePath, ".json");
            lock (_lock)
            {
                _events.Clear();
            }
        }

        public void LogEvent(string type, string description, TimeSpan timestamp)
        {
            var newItem = new EventItem
            {
                Timestamp = timestamp,
                Type = type,
                Description = description
            };

            lock (_lock)
            {
                _events.Add(newItem);
            }

            // Save immediately or buffer? For safety, save immediately for now.
            // In high freq scenario, we should buffer.
            Task.Run(() => SaveLog());
        }

        private void SaveLog()
        {
            try
            {
                List<EventItem> snapshot;
                lock (_lock)
                {
                    snapshot = new List<EventItem>(_events);
                }

                var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_logFilePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save log: {ex.Message}");
            }
        }
        
        public List<EventItem> LoadLog(string jsonPath)
        {
             if (!File.Exists(jsonPath)) return new List<EventItem>();
             try
             {
                 var json = File.ReadAllText(jsonPath);
                 return JsonSerializer.Deserialize<List<EventItem>>(json) ?? new List<EventItem>();
             }
             catch 
             {
                 return new List<EventItem>();
             }
        }
    }

    public class EventItem
    {
        public TimeSpan Timestamp { get; set; }
        public string Type { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
    }
}
