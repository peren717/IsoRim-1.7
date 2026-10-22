using System;
using System.Collections.Generic;
using LudeonTK;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // A searchable list for the model binder (the .obj picker, "Copy from existing"): entries grouped under headers; clicking
    // one runs its action and closes the list. Typing in the search box doesn't move the camera (preventCameraMotion while
    // it has focus).
    public class BinderList : EditWindow
    {
        public struct Entry
        {
            public string group;
            public string label;
            public string tip;
            public Action pick;
        }

        private readonly string title;
        private readonly List<Entry> entries;
        private string search = "";
        private Vector2 scroll;
        private bool focusDone;
        private const string FieldName = "IsoRimBinder_search";

        public BinderList(string title, List<Entry> entries)
        {
            this.title = title;
            this.entries = entries;
            optionalTitle = title;
            closeOnClickedOutside = false;
            closeOnAccept = false;
        }

        public int Count
        {
            get { return entries.Count; }
        }

        // Types into the search box and picks the first entry that matches, as a click on it does (harness probe).
        public bool PickFirst(string text)
        {
            search = text;
            foreach (Entry e in entries)
            {
                if (e.label.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Close(false);
                    e.pick();
                    return true;
                }
            }
            return false;
        }

        public override Vector2 InitialSize
        {
            get { return new Vector2(460f, 560f); }
        }

        public override void DoWindowContents(Rect inRect)
        {
            preventCameraMotion = GUI.GetNameOfFocusedControl() == FieldName;
            Text.Font = GameFont.Small;
            Rect searchRect = new Rect(inRect.x, inRect.y, inRect.width, 26f);
            GUI.SetNextControlName(FieldName);
            search = Widgets.TextField(searchRect, search);
            if (!focusDone)
            {
                GUI.FocusControl(FieldName);
                focusDone = true;
            }
            if (search.NullOrEmpty())
            {
                GUI.color = Color.grey;
                Widgets.Label(new Rect(searchRect.x + 6f, searchRect.y + 3f, searchRect.width, 22f), "R3D_Binder_Search".Translate());
                GUI.color = Color.white;
            }

            List<Entry> shown = new List<Entry>();
            string s = search.Trim();
            foreach (Entry e in entries)
            {
                if (s.Length == 0 || e.label.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0
                    || (e.group != null && e.group.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    shown.Add(e);
                }
            }
            Rect outRect = new Rect(inRect.x, inRect.y + 32f, inRect.width, inRect.height - 32f);
            float rowH = 22f;
            int headers = 0;
            string last = null;
            foreach (Entry e in shown)
            {
                if (e.group != last)
                {
                    headers++;
                    last = e.group;
                }
            }
            Rect viewRect = new Rect(0f, 0f, outRect.width - 16f, (shown.Count + headers) * rowH);
            Widgets.BeginScrollView(outRect, ref scroll, viewRect);
            float y = 0f;
            last = null;
            int first = Mathf.FloorToInt(scroll.y / rowH) - 2;
            int lastRow = first + Mathf.CeilToInt(outRect.height / rowH) + 4;
            int row = 0;
            foreach (Entry e in shown)
            {
                if (e.group != last)
                {
                    last = e.group;
                    if (row >= first && row <= lastRow)
                    {
                        GUI.color = new Color(1f, 0.85f, 0.5f);
                        Widgets.Label(new Rect(0f, y, viewRect.width, rowH), e.group ?? "");
                        GUI.color = Color.white;
                    }
                    y += rowH;
                    row++;
                }
                if (row >= first && row <= lastRow)
                {
                    Rect r = new Rect(12f, y, viewRect.width - 12f, rowH);
                    Widgets.DrawHighlightIfMouseover(r);
                    Widgets.Label(r, e.label);
                    if (!e.tip.NullOrEmpty())
                    {
                        TooltipHandler.TipRegion(r, e.tip);
                    }
                    if (Widgets.ButtonInvisible(r))
                    {
                        Action pick = e.pick;
                        Close(false);
                        pick();
                    }
                }
                y += rowH;
                row++;
            }
            Widgets.EndScrollView();
        }
    }
}
