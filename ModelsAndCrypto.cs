using System;
using System.IO;
using System.Management;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WebDavEncryptManager
{
    public class FileItem
    {
        public string Name { get; set; }
        public string FullPath { get; set; }
        public bool IsDirectory { get; set; }
        public long Size { get; set; }
        public string Icon => IsDirectory ? "📁" : "📄";

        public string DisplaySize
        {
            get
            {
                if (IsDirectory) return "";
                string[] sizes = { "B", "KB", "MB", "GB", "TB" };
                double len = Size;
                int order = 0;
                while (len >= 1024 && order < sizes.Length - 1)
                {
                    order++;
                    len = len / 1024;
                }
                return $"{len:0.##} {sizes[order]}";
            }
        }
    }

    public class AppConfig
    {
        public string WebDavUrl { get; set; } = "";
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
    }

    public static class HardwareCryptoHelper
    {
        private static string GetHardwareId()
        {
            try
            {
                string id = "";
                using (var searcher = new ManagementObjectSearcher("select ProcessorId from Win32_Processor"))
                {
                    foreach (var item in searcher.Get()) { id += item["ProcessorId"]?.ToString(); break; }
                }
                using (var searcher = new ManagementObjectSearcher("select SerialNumber from Win32_BaseBoard"))
                {
                    foreach (var item in searcher.Get()) { id += item["SerialNumber"]?.ToString(); break; }
                }
                return string.IsNullOrEmpty(id) ? "FALLBACK_ID" : id;
            }
            catch { return "FALLBACK_ID"; }
        }

        private static byte[] GetMachineKey()
        {
            using (SHA256 sha256 = SHA256.Create())
                return sha256.ComputeHash(Encoding.UTF8.GetBytes(GetHardwareId() + "WebDavSalt"));
        }

        public static void SaveEncryptedConfig(AppConfig config, string filePath)
        {
            byte[] key = GetMachineKey();
            byte[] iv = new byte[16];
            Array.Copy(key, iv, 16);
            using (Aes aes = Aes.Create())
            {
                aes.Key = key; aes.IV = iv;
                using (FileStream fs = new FileStream(filePath, FileMode.Create))
                using (CryptoStream cs = new CryptoStream(fs, aes.CreateEncryptor(), CryptoStreamMode.Write))
                using (StreamWriter sw = new StreamWriter(cs))
                {
                    sw.Write(JsonSerializer.Serialize(config));
                }
            }
        }

        public static AppConfig LoadEncryptedConfig(string filePath)
        {
            if (!File.Exists(filePath)) return new AppConfig();
            try
            {
                byte[] key = GetMachineKey();
                byte[] iv = new byte[16];
                Array.Copy(key, iv, 16);
                using (FileStream fs = new FileStream(filePath, FileMode.Open))
                using (Aes aes = Aes.Create())
                {
                    aes.Key = key; aes.IV = iv;
                    using (CryptoStream cs = new CryptoStream(fs, aes.CreateDecryptor(), CryptoStreamMode.Read))
                    using (StreamReader sr = new StreamReader(cs))
                    {
                        return JsonSerializer.Deserialize<AppConfig>(sr.ReadToEnd()) ?? new AppConfig();
                    }
                }
            }
            catch { return new AppConfig(); }
        }
    }
}