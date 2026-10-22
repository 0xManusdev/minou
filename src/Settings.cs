using System;
using System.Collections.Generic;
using System.IO;

namespace Minou
{
    internal sealed class Settings
    {
        /// <summary>"primary", "active" (screen of the foreground window) or a device name such as \\.\DISPLAY2.</summary>
        public string Display = "primary";
        public bool SoundEnabled = true;
        public double SoundVolume = 0.35;
        public string AttentionSound = "Windows Notify Messaging.wav";
        public string FinishSound = "Windows Notify Calendar.wav";
        public bool HideInFullscreen = true;
        public bool ShowIdlePill = true;
        /// <summary>Stay in the tray after the last Claude Code session closes.</summary>
        public bool StayOpen = false;
        /// <summary>Set once the tray icon has been pulled out of the overflow, so a later "hide" by the user sticks.</summary>
        public bool TrayPromoted = false;

        public static readonly string[] Sounds =
        {
            "Windows Notify Messaging.wav", "Windows Notify Calendar.wav", "Windows Notify Email.wav",
            "Windows Notify System Generic.wav", "Windows Notify.wav", "Windows Ding.wav",
            "Windows Message Nudge.wav", "Windows Proximity Notification.wav", "Windows Balloon.wav"
        };

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (!File.Exists(Paths.SettingsFile)) return s;
                Dictionary<string, object> d = Json.ParseObject(File.ReadAllText(Paths.SettingsFile));
                if (d == null) return s;
                s.Display = Json.Str(d, "display") ?? s.Display;
                s.SoundEnabled = Json.Bool(d, "soundEnabled", s.SoundEnabled);
                object vol;
                if (d.TryGetValue("soundVolume", out vol) && vol != null)
                    s.SoundVolume = Math.Max(0, Math.Min(1, Convert.ToDouble(vol, System.Globalization.CultureInfo.InvariantCulture)));
                s.AttentionSound = Json.Str(d, "attentionSound") ?? s.AttentionSound;
                s.FinishSound = Json.Str(d, "finishSound") ?? s.FinishSound;
                s.HideInFullscreen = Json.Bool(d, "hideInFullscreen", s.HideInFullscreen);
                s.ShowIdlePill = Json.Bool(d, "showIdlePill", s.ShowIdlePill);
                s.StayOpen = Json.Bool(d, "stayOpen", s.StayOpen);
                s.TrayPromoted = Json.Bool(d, "trayPromoted", s.TrayPromoted);
            }
            catch (Exception ex)
            {
                Log.Write("settings: " + ex.Message);
            }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Paths.DataDir);
                var d = new Dictionary<string, object>();
                d["display"] = Display;
                d["soundEnabled"] = SoundEnabled;
                d["soundVolume"] = SoundVolume;
                d["attentionSound"] = AttentionSound;
                d["finishSound"] = FinishSound;
                d["hideInFullscreen"] = HideInFullscreen;
                d["showIdlePill"] = ShowIdlePill;
                d["stayOpen"] = StayOpen;
                d["trayPromoted"] = TrayPromoted;
                File.WriteAllText(Paths.SettingsFile, Json.Pretty(d));
            }
            catch (Exception ex)
            {
                Log.Write("settings save: " + ex.Message);
            }
        }
    }
}
