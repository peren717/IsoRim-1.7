using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Harness-only shots of the mod settings window (for checking its translations), driven by the probe file (see Probe):
    //   settingswin=<tab>,<tag>[,bottom]   opens IsoRim's settings window on that tab (0 view, 4 see-through, 1 colour, 5 graphics,
    //                                      2 ground, 3 what is 3D), scrolled to the bottom with "bottom", and ~40 frames
    //                                      later takes a shot with the interface on: renders\<harness tag>\settings_<tag>.png
    //   settingswin=<tab>,<tag>,tips       as above, with the tooltip of every option an addon has locked drawn under it
    //                                      (the hidden harness window has no pointer to hover it)
    //   settingswin=close                  closes it again
    //   tiltsettings=<label>[,write]       logs the live tilt-shift settings and whether their keys are in the settings file;
    //                                      with "write" the settings are written and read back first (round trip, CHECK ok/FAILED)
    // Other options for a shot are set in the same pass first (set=perspective,1).
    public static partial class Probe
    {
        private static string settingsShot;
        private static int settingsShotFrame = -1;

        // Harness only: SettingsWindow.LockedCheck draws its tooltip under the option (settingswin=...,tips).
        public static bool ShowLockTips;

        private static void SettingsWin(string[] a)
        {
            if (a[0] == "close")
            {
                Find.WindowStack.TryRemove(typeof(Dialog_ModSettings), doCloseSound: false);
                Find.UIRoot.screenshotMode.Active = true;
                ShowLockTips = false;
                return;
            }
            ShowLockTips = a.Length > 2 && a[2] == "tips";
            AccessTools.Field(typeof(SettingsWindow), "tab").SetValue(null, int.Parse(a[0]));
            if (!Find.WindowStack.IsOpen(typeof(Dialog_ModSettings)))
            {
                Find.WindowStack.Add(new Dialog_ModSettings(LoadedModManager.GetMod<IsoRimMod>()));
            }
            // The window resets the scroll when the tab changes: set it after the first frame on the tab (see SettingsShotTick).
            settingsBottom = a.Length > 2 && a[2] == "bottom";
            Find.UIRoot.screenshotMode.Active = false;
            settingsShot = a[1];
            settingsShotFrame = Time.frameCount + 40;
        }

        private static bool settingsBottom;

        // Every frame from Begin.
        private static void SettingsShotTick()
        {
            if (settingsShot == null)
            {
                return;
            }
            if (settingsBottom && Time.frameCount == settingsShotFrame - 20)
            {
                AccessTools.Field(typeof(SettingsWindow), "scroll").SetValue(null, new Vector2(0f, 100000f));
            }
            if (Time.frameCount < settingsShotFrame)
            {
                return;
            }
            string dir = System.IO.Path.Combine(GenFilePaths.SaveDataFolderPath, "renders", HarnessTag());
            System.IO.Directory.CreateDirectory(dir);
            ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(dir, "settings_" + settingsShot + ".png"));
            Log.Message("[IsoRim probe] settings shot " + settingsShot + " (language " + LanguageDatabase.activeLanguage?.folderName + ")");
            settingsShot = null;
        }

        private static string TiltText(IsoRimSettings s)
        {
            return "tiltShift " + s.tiltShift + " tiltStrength " + s.tiltStrength + " tiltCentre " + s.tiltCentre + " tiltWidth " + s.tiltWidth + " tiltPop " + s.tiltPop;
        }

        private static void TiltSettings(string[] a)
        {
            Mod mod = LoadedModManager.GetMod<IsoRimMod>();
            IsoRimSettings live = IsoRimMod.Settings;
            string result = "";
            if (a.Length > 1 && a[1] == "write")
            {
                mod.WriteSettings();
                IsoRimSettings read = LoadedModManager.ReadModSettings<IsoRimSettings>(mod.Content.FolderName, mod.GetType().Name);
                bool ok = read.tiltShift == live.tiltShift && read.tiltStrength == live.tiltStrength && read.tiltCentre == live.tiltCentre
                    && read.tiltWidth == live.tiltWidth && read.tiltPop == live.tiltPop;
                result = ", read back: " + TiltText(read) + " -> " + (ok ? "CHECK ok" : "CHECK FAILED");
            }
            string file = System.IO.Path.Combine(GenFilePaths.ConfigFolderPath, GenText.SanitizeFilename("Mod_" + mod.Content.FolderName + "_" + mod.GetType().Name + ".xml"));
            string text = System.IO.File.Exists(file) ? System.IO.File.ReadAllText(file) : "";
            int keys = 0;
            foreach (string k in new[] { "tiltShift", "tiltStrength", "tiltCentre", "tiltWidth", "tiltPop" })
            {
                if (text.Contains("<" + k + ">"))
                {
                    keys++;
                }
            }
            Log.Message("[IsoRim probe] tiltsettings " + a[0] + ": live " + TiltText(live) + ", tilt keys in file " + keys + "/5" + result);
        }
    }
}
