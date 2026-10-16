using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;

namespace StillsAndReels
{
    /// <summary>Small key=value settings file in %APPDATA%\StillsAndReels\settings.ini (moved from the old Picture Preview folder on first run).</summary>
    internal sealed class Settings
    {
        readonly Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly string path;

        public Settings()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            path = Path.Combine(appData, "StillsAndReels", "settings.ini");
            try
            {
                // keep the user's settings from when the app was called Picture Preview
                string old = Path.Combine(appData, "PicturePreview", "settings.ini");
                if (!File.Exists(path) && File.Exists(old))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.Copy(old, path);
                }
            }
            catch { }
            try
            {
                if (File.Exists(path))
                {
                    foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
                    {
                        int eq = line.IndexOf('=');
                        if (eq > 0) values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                    }
                }
            }
            catch { }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var sb = new StringBuilder();
                foreach (var kv in values) sb.Append(kv.Key).Append('=').Append(kv.Value).AppendLine();
                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        public int GetInt(string key, int def)
        {
            string s; int v;
            return values.TryGetValue(key, out s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : def;
        }

        public bool GetBool(string key, bool def)
        {
            string s;
            return values.TryGetValue(key, out s) ? s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase) : def;
        }

        public Color GetColor(string key, Color def)
        {
            string s; int v;
            if (values.TryGetValue(key, out s) && int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v))
                return Color.FromArgb(v);
            return def;
        }

        public string GetString(string key, string def)
        {
            string s;
            return values.TryGetValue(key, out s) ? s : def;
        }

        public void Set(string key, string v) { values[key] = (v ?? "").Replace("\r", " ").Replace("\n", " "); }

        public void Set(string key, int v) { values[key] = v.ToString(CultureInfo.InvariantCulture); }
        public void Set(string key, bool v) { values[key] = v ? "1" : "0"; }
        public void Set(string key, Color c) { values[key] = c.ToArgb().ToString("X8", CultureInfo.InvariantCulture); }
    }
}
