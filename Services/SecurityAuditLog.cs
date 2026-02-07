using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TimeLapseCam.Services
{
    /// <summary>
    /// Tamper-proof security audit log using a hash chain.
    /// Each entry includes a SHA-256 hash of (previous_hash + entry_data),
    /// making it impossible to modify or delete entries without detection.
    /// </summary>
    public class SecurityAuditLog
    {
        private readonly string _localLogPath;
        private readonly string? _cloudLogPath;
        private string _lastHash = "GENESIS";
        private readonly object _lock = new();

        public SecurityAuditLog(string localFolder, string? cloudFolder)
        {
            string dateFolder = DateTime.Now.ToString("yyyy-MM-dd");

            string localDir = Path.Combine(localFolder, "TimeLapseCam", "AuditLog", dateFolder);
            if (!Directory.Exists(localDir)) Directory.CreateDirectory(localDir);
            _localLogPath = Path.Combine(localDir, "security_audit.jsonl");

            if (cloudFolder != null)
            {
                string cloudDir = Path.Combine(cloudFolder, "TimeLapseCam", "AuditLog", dateFolder);
                if (!Directory.Exists(cloudDir)) Directory.CreateDirectory(cloudDir);
                _cloudLogPath = Path.Combine(cloudDir, "security_audit.jsonl");
            }

            // Load last hash from existing log
            LoadLastHash();
        }

        private void LoadLastHash()
        {
            try
            {
                if (File.Exists(_localLogPath))
                {
                    string[] lines = File.ReadAllLines(_localLogPath);
                    if (lines.Length > 0)
                    {
                        var lastEntry = JsonSerializer.Deserialize<AuditEntry>(lines[^1]);
                        if (lastEntry != null)
                            _lastHash = lastEntry.Hash;
                    }
                }
            }
            catch { }
        }

        public void Log(string eventType, string message)
        {
            lock (_lock)
            {
                try
                {
                    var entry = new AuditEntry
                    {
                        Timestamp = DateTime.UtcNow.ToString("o"),
                        EventType = eventType,
                        Message = message,
                        PreviousHash = _lastHash,
                    };

                    // Compute hash: SHA-256(previousHash + timestamp + eventType + message)
                    string dataToHash = $"{entry.PreviousHash}|{entry.Timestamp}|{entry.EventType}|{entry.Message}";
                    entry.Hash = ComputeSHA256(dataToHash);
                    _lastHash = entry.Hash;

                    string json = JsonSerializer.Serialize(entry);

                    // Append to local log
                    File.AppendAllText(_localLogPath, json + Environment.NewLine);

                    // Append to cloud log
                    if (_cloudLogPath != null)
                    {
                        File.AppendAllText(_cloudLogPath, json + Environment.NewLine);
                    }

                    Debug.WriteLine($"[AuditLog] {entry.EventType}: {entry.Message} (hash: {entry.Hash[..8]}...)");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[AuditLog] Error: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Verify the integrity of the log by re-computing all hashes.
        /// Returns (isValid, errorMessage).
        /// </summary>
        public static (bool IsValid, string Message) VerifyLog(string logFilePath)
        {
            try
            {
                if (!File.Exists(logFilePath))
                    return (false, "Log file not found");

                string[] lines = File.ReadAllLines(logFilePath);
                if (lines.Length == 0)
                    return (true, "Empty log");

                string previousHash = "GENESIS";

                for (int i = 0; i < lines.Length; i++)
                {
                    var entry = JsonSerializer.Deserialize<AuditEntry>(lines[i]);
                    if (entry == null)
                        return (false, $"Line {i + 1}: Invalid JSON");

                    if (entry.PreviousHash != previousHash)
                        return (false, $"Line {i + 1}: Hash chain broken — previous hash mismatch (expected {previousHash[..8]}..., got {entry.PreviousHash[..8]}...)");

                    string expectedHash = ComputeSHA256($"{entry.PreviousHash}|{entry.Timestamp}|{entry.EventType}|{entry.Message}");
                    if (entry.Hash != expectedHash)
                        return (false, $"Line {i + 1}: Entry hash invalid — data was modified");

                    previousHash = entry.Hash;
                }

                return (true, $"Log verified: {lines.Length} entries, chain intact ✓");
            }
            catch (Exception ex)
            {
                return (false, $"Verification error: {ex.Message}");
            }
        }

        private static string ComputeSHA256(string input)
        {
            byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }
    }

    public class AuditEntry
    {
        public string Timestamp { get; set; } = string.Empty;
        public string EventType { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string PreviousHash { get; set; } = string.Empty;
        public string Hash { get; set; } = string.Empty;
    }
}
