using System;
using System.Reflection;
using System.Xml;
using Verse;

namespace IsoRimPackGate
{
    // For XML model packs for IsoRim Waylith (Docs/ModelPacks.md): runs its operation (apply) only when IsoRim is loaded and
    // its Api.Version is at least minVersion; otherwise does nothing and counts as done (no patch error). Every pack ships
    // the same IsoRimPackGate.dll in 1.7/Assemblies; it needs no reference to IsoRim (it reads IsoRim.Api.Version by name),
    // so a pack loads with any IsoRim and without one.
    //
    //   <Operation Class="IsoRimPackGate.PatchOperation_IfIsoRimApi">
    //     <minVersion>2</minVersion>
    //     <apply Class="PatchOperationAdd">
    //       <xpath>/Defs/IsoRim.ModelDef[defName="R3D_MyPack_Thing"]</xpath>
    //       <value><thingDef>Thing</thingDef></value>
    //     </apply>
    //   </Operation>
    public class PatchOperation_IfIsoRimApi : PatchOperation
    {
        public int minVersion;
        public PatchOperation apply;

        protected override bool ApplyWorker(XmlDocument xml)
        {
            if (apply == null || IsoRimApi() < minVersion)
            {
                return true;
            }
            return apply.Apply(xml);
        }

        private static int cached = -1;

        // IsoRim's Api.Version, 0 if IsoRim isn't loaded.
        public static int IsoRimApi()
        {
            if (cached >= 0)
            {
                return cached;
            }
            int v = 0;
            try
            {
                Type api = GenTypes.GetTypeInAnyAssembly("IsoRim.Api");
                PropertyInfo p = api != null ? api.GetProperty("Version", BindingFlags.Public | BindingFlags.Static) : null;
                if (p != null)
                {
                    v = (int)p.GetValue(null);
                }
            }
            catch (Exception)
            {
                v = 0;
            }
            cached = v;
            return v;
        }
    }
}
