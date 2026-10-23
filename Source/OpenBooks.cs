using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // What a pawn holds while it reads (JobDriver_Reading sets Book.IsOpen for the reading toil and clears it after): the book
    // opened (Models/open_book.obj: TextBook, Novel, Tome and any other book), the map unfolded (open_map.obj: Map) or the folder
    // opened (open_schematic.obj: Schematic), tinted with the book sprite's mean colour as the closed book is. Drawn by
    // Carried.Draw in the reader's hands; the closed book comes back when IsOpen is cleared.
    public static class OpenBooks
    {
        private static readonly Dictionary<string, string> ByDef = new Dictionary<string, string>
        {
            { "Map", "open_map.obj" },
            { "Schematic", "open_schematic.obj" },
        };

        private const string Default = "open_book.obj";
        private static readonly Dictionary<string, Model> loaded = new Dictionary<string, Model>();

        // The model to draw for this book while it is open, or null (not a book, closed, or the model is missing).
        public static Model For(Thing t)
        {
            Book book = t as Book;
            if (book == null || !book.IsOpen || IsoRimMod.RootDir == null)
            {
                return null;
            }
            string file;
            if (!ByDef.TryGetValue(t.def.defName, out file))
            {
                file = Default;
            }
            lock (loaded)
            {
                Model m;
                if (!loaded.TryGetValue(file, out m))
                {
                    m = ModelLibrary.LoadFile(System.IO.Path.Combine(IsoRimMod.RootDir, "Models", file));
                    loaded[file] = m;
                }
                return m;
            }
        }
    }
}
