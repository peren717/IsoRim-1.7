using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Api 25: the "Keep as paper" setting (IsoRimSettings.keepPaper): a button under the person figures that opens a list of
    // the loaded humanlike races other than people; ticked races stay cards even when an alien pack makes them 3D. Shown only
    // when such races are loaded.
    internal static class AliensUI
    {
        private static List<ThingDef> races;

        internal static void KeepAsPaper(Listing_Standard l, IsoRimSettings st)
        {
            if (races == null)
            {
                races = AlienFigures.OtherAliens();
            }
            if (races.Count == 0)
            {
                return;
            }
            Rect r = l.GetRect(28f);
            string label = "R3D_Settings_KeepAsPaper".Translate() + (st.keepPaper.Count > 0 ? " (" + st.keepPaper.Count + ")" : "");
            TooltipHandler.TipRegion(r, "R3D_Settings_KeepAsPaperTip".Translate());
            if (Widgets.ButtonText(r.LeftPart(0.7f), label))
            {
                Find.WindowStack.Add(new Dialog_KeepAsPaper(races, st));
            }
            l.Gap(l.verticalSpacing);
        }
    }

    internal class Dialog_KeepAsPaper : Window
    {
        private readonly List<ThingDef> races;
        private readonly IsoRimSettings st;
        private Vector2 scroll;

        public Dialog_KeepAsPaper(List<ThingDef> races, IsoRimSettings st)
        {
            this.races = races;
            this.st = st;
            doCloseX = true;
            doCloseButton = true;
            closeOnClickedOutside = true;
            absorbInputAroundWindow = true;
        }

        public override Vector2 InitialSize => new Vector2(420f, Mathf.Min(620f, 140f + races.Count * 28f));

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 34f), "R3D_Settings_KeepAsPaper".Translate());
            Text.Font = GameFont.Small;
            Rect outer = new Rect(inRect.x, inRect.y + 40f, inRect.width, inRect.height - 40f - CloseButSize.y - 10f);
            Rect view = new Rect(0f, 0f, outer.width - 16f, races.Count * 28f);
            Widgets.BeginScrollView(outer, ref scroll, view);
            for (int i = 0; i < races.Count; i++)
            {
                ThingDef d = races[i];
                bool on = st.keepPaper.Contains(d.defName);
                bool was = on;
                Widgets.CheckboxLabeled(new Rect(0f, i * 28f, view.width, 26f), d.LabelCap + "  (" + d.defName + ")", ref on);
                if (on != was)
                {
                    if (on)
                    {
                        st.keepPaper.Add(d.defName);
                    }
                    else
                    {
                        st.keepPaper.Remove(d.defName);
                    }
                    AlienFigures.Clear();
                }
            }
            Widgets.EndScrollView();
        }
    }
}
