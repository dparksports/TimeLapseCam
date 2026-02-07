using System;
using System.IO;
using System.Text.Json;
using System.Collections.Generic;
using Windows.Storage;

namespace TimeLapseCam.Services
{
    public static class SettingsHelper
    {
        private static Dictionary<string, object> _cache = new();
        private static readonly string _filePath = Path.Combine(AppContext.BaseDirectory, "settings.json");

        static SettingsHelper()
        {
            Load();
        }

        public static T Get<T>(string key, T defaultValue)
        {
            if (_cache.TryGetValue(key, out var value))
            {
                if (value is JsonElement element)
                {
                    return element.Deserialize<T>() ?? defaultValue;
                }
                return (T)value;
            }
            return defaultValue;
        }

        public static void Set<T>(string key, T value)
        {
            _cache[key] = value!;
            Save();
        }

        public static void Remove(string key)
        {
            if (_cache.Remove(key))
            {
                Save();
            }
        }

        private static void Load()
        {
            try
            {
                if (File.Exists(_filePath))
                {
                    var json = File.ReadAllText(_filePath);
                    _cache = JsonSerializer.Deserialize<Dictionary<string, object>>(json) ?? new();
                }
            }
            catch (Exception)
            {
                _cache = new();
            }
        }

        private static void Save()
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(_cache, options);
                File.WriteAllText(_filePath, json);
            }
            catch (Exception)
            {
                // Ignore save errors
            }
        }
    }
}
