using System;
using System.IO;
using System.Text;
using System.Text.Json;
using KeyMacro.Models;

namespace KeyMacro.Services
{
    /// <summary>配置读写(JSON,UTF-8)。配置文件与 exe 同目录。</summary>
    public static class ConfigStore
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
        };

        public static string ConfigPath
        {
            get
            {
                string dir = AppContext.BaseDirectory;
                return Path.Combine(dir, "config.json");
            }
        }

        public static AppConfig Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath, Encoding.UTF8);
                    var cfg = JsonSerializer.Deserialize<AppConfig>(json, Options);
                    if (cfg != null) return cfg;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("读取配置失败: " + ex.Message);
            }
            return new AppConfig();
        }

        public static void Save(AppConfig config)
        {
            try
            {
                string json = JsonSerializer.Serialize(config, Options);
                File.WriteAllText(ConfigPath, json, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("保存配置失败: " + ex.Message);
            }
        }
    }
}
