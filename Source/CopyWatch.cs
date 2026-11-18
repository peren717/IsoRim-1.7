using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using LudeonTK;
using Verse;

namespace IsoRim
{
    // Api 70 (Api.WatchCopy, Api.CopyHash): an addon's copies of other methods, checked against the IL of the original once
    // the loading step they were registered in has finished. Nothing registered: nothing here runs.
    //
    // The hash: SHA-256 over a text made from the method's IL bytes as compiled (CompiledIL below: the body the assembly
    // was built with, whatever Harmony patches the method carries now), instruction by instruction: the opcode, then its
    // operand as follows.
    // - Numbers, local and argument indexes, branch offsets (relative, in bytes) and switch tables: their bytes as hex.
    // - Metadata tokens (methods, fields, types, ldtoken) resolved to names: type names are namespace.Name with generic
    //   arguments, nested types after "/", generic parameters as !n (type) or !!n (method), no assembly names or versions;
    //   methods are Type::Name<args>(parameter types):return type; fields Type::Name:field type. Digit runs inside
    //   compiler-made names (those holding '<': lambdas, display classes, iterator classes) count as one "#", since their
    //   numbers move when other methods of the class are added.
    // - String literals: their text. calli signatures: only "sig" (their blob holds tokens of its own).
    // Not hashed: the method's signature, locals, exception clauses, and the bodies of lambdas or iterators it uses (those
    // are separate methods: watch them too where the copied logic lives there).
    internal static class CopyWatch
    {
        sealed class Watch
        {
            public MethodBase method;
            public string expected;
            public string note;
            public bool done;
        }

        static readonly object Gate = new object();
        static readonly List<Watch> All = new List<Watch>();
        static readonly List<Watch> Pending = new List<Watch>();
        static bool flushQueued;

        internal static void Add(MethodBase method, string expected, string note)
        {
            try
            {
                lock (Gate)
                {
                    string want = (expected ?? "").Trim().ToLowerInvariant();
                    for (int i = 0; i < All.Count; i++)
                    {
                        Watch o = All[i];
                        if (o.method == method && o.expected == want && o.note == note)
                        {
                            return;
                        }
                    }
                    Watch w = new Watch { method = method, expected = want, note = note };
                    All.Add(w);
                    Pending.Add(w);
                    if (flushQueued)
                    {
                        return;
                    }
                    flushQueued = true;
                }
                // Mod constructors run on the loading thread while the game's load event is current: queued, the check runs
                // on the main thread once that event (all mods, defs) has finished. From a static constructor it runs after
                // the static constructors; with no loading step under way it runs at once.
                try
                {
                    LongEventHandler.ExecuteWhenFinished(Flush);
                }
                catch
                {
                    Flush();
                }
            }
            catch (Exception e)
            {
                try
                {
                    Log.Warning("[IsoRim] Api.WatchCopy (" + note + "): " + e.Message);
                }
                catch
                {
                }
            }
        }

        // A watch checked now, not once the loading step has finished (MeshFill's two, from Patches.ApplyAll). Same hash
        // and same warning as any other watch: the body as compiled is read (CompiledIL), so the method may carry
        // patches, the caller's own or anyone's. (Until 2026-11-18 the hash followed the patches and this had to run
        // before the method's first patch; on a host without the runtime's reader it still follows them.)
        internal static void AddNow(MethodBase method, string expected, string note)
        {
            try
            {
                Watch w = new Watch { method = method, expected = (expected ?? "").Trim().ToLowerInvariant(), note = note };
                lock (Gate)
                {
                    All.Add(w);
                }
                Check(w);
            }
            catch
            {
            }
        }

        static void Flush()
        {
            List<Watch> now;
            lock (Gate)
            {
                now = new List<Watch>(Pending);
                Pending.Clear();
                flushQueued = false;
            }
            for (int i = 0; i < now.Count; i++)
            {
                Check(now[i]);
            }
        }

        static void Check(Watch w)
        {
            try
            {
                lock (Gate)
                {
                    if (w.done)
                    {
                        return;
                    }
                    w.done = true;
                }
                string problem = Problem(w);
                if (problem != null)
                {
                    Log.Warning("[IsoRim] " + w.note + ": " + problem + "; it may need an update.");
                }
            }
            catch
            {
            }
        }

        // The warning's text without the note and the closing words, null when the method is as copied.
        static string Problem(Watch w)
        {
            if (w.method == null)
            {
                return "the method this was copied from was not found";
            }
            string now = Hash(w.method, out string error);
            if (now == null)
            {
                return Name(w.method) + " could not be read (" + error + ")";
            }
            return now == w.expected ? null : Name(w.method) + " has changed since this was copied";
        }

        static string Name(MethodBase m)
        {
            Type t = m.DeclaringType;
            return (t == null ? "" : (t.FullName ?? t.Name) + ".") + m.Name;
        }

        [DebugAction("IsoRim", "Log copied method hashes", allowedGameStates = AllowedGameStates.Entry | AllowedGameStates.Playing)]
        internal static void LogHashes()
        {
            List<Watch> list;
            lock (Gate)
            {
                list = new List<Watch>(All);
            }
            // (Said here only: the choice of reading is silent at load.)
            string reading = !ReflectionOnly && HasHostReader
                ? "Method bodies are read as compiled, from the runtime's own record: patches do not change a hash."
                : "Method bodies are read through reflection (this runtime gives no record of them as compiled): a patched method's hash follows its patches.";
            if (list.Count == 0)
            {
                Log.Message("[IsoRim] No copied methods are watched (Api.WatchCopy).\n  " + reading);
                return;
            }
            StringBuilder sb = new StringBuilder("[IsoRim] Copied methods watched (Api.WatchCopy): " + list.Count);
            sb.Append("\n  ").Append(reading);
            for (int i = 0; i < list.Count; i++)
            {
                Watch w = list[i];
                string now = w.method == null ? null : Hash(w.method, out _);
                sb.Append("\n  ").Append(w.note).Append(": ").Append(w.method == null ? "(not found)" : Name(w.method))
                  .Append(" = ").Append(now ?? "-").Append(" (registered ").Append(w.expected.Length == 0 ? "-" : w.expected)
                  .Append(now != null && now == w.expected ? ", same)" : ", differs)");
            }
            Log.Message(sb.ToString());
        }

        // Harness (ProbeWatchCopy): every registered watch, in the order registered: its method (null: not found), the
        // hash registered and the note.
        internal static void Each(Action<MethodBase, string, string> visit)
        {
            List<Watch> list;
            lock (Gate)
            {
                list = new List<Watch>(All);
            }
            for (int i = 0; i < list.Count; i++)
            {
                visit(list[i].method, list[i].expected, list[i].note);
            }
        }

        // ---- the hash

        // The opcode tables, built once on first use by the runtime's type initialiser (thread-safe).
        static class OpTable
        {
            internal static readonly OpCode[] One = new OpCode[256];
            internal static readonly OpCode[] Two = new OpCode[256];

            static OpTable()
            {
                Build(One, Two);
            }
        }

        static void Build(OpCode[] one, OpCode[] two)
        {
            foreach (FieldInfo f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (f.FieldType != typeof(OpCode))
                {
                    continue;
                }
                OpCode op = (OpCode)f.GetValue(null);
                ushort v = (ushort)op.Value;
                if (op.Size == 1)
                {
                    one[v] = op;
                }
                else if ((v & 0xff00) == 0xfe00)
                {
                    two[v & 0xff] = op;
                }
            }
        }

        // ---- the IL as compiled
        //
        // On the 1.7 host Harmony patches a method by replacing its body in the runtime (WaylithClr.MethodBodyUpdater in
        // System.Private.CoreLib), and reflection (MethodBase.GetMethodBody) then gives the replaced body: read that way,
        // a method hashed differently with every set of patches on it (measured 2026-11-17 and 11-18, game 1.7.5013).
        // The runtime keeps the body the assembly was built with, and gives it through
        //     MethodBodyUpdater.TryGetDefaultMethodBody(MethodBase, Span<MethodBodyClause>, out MethodBodyView)
        // (MethodBodyView.IL: ReadOnlySpan<byte>). That is the body the runtime's own transformer pipeline starts from
        // when it rebuilds a method (MethodBodyTransformer's Pipeline.TryLoadInput, before any stage has run; Harmony is
        // one stage of it and calls this reader itself only to decide whether it can patch a method). HostBody.Read is
        // that call.
        // - By name, never a typed reference: a host type or signature that goes away must not stop this class (or any
        //   of IsoRim) from loading. A Span argument and an out struct read through a span cannot go through
        //   MethodInfo.Invoke (byref-like values cannot be boxed), so one small method is emitted (DynamicMethod, once)
        //   that makes the call with an empty clause span (the IL needs none) and returns the IL as a byte[], null when
        //   the host says it has no body.
        // - Decided once, at first use: the host has no such type or method, says IsSupported false, the shapes are not
        //   those above, the emit fails, or a trial on this class's own Build (which nobody patches) throws or does not
        //   give exactly the bytes reflection gives: Read stays null for the session, silently.
        // - Per method: Read gives nothing or throws for it: reflection for that method.
        // In each of those cases the reading is reflection's, as it always was before; nothing here throws to the caller.
        static class HostBody
        {
            internal static readonly Func<MethodBase, byte[]> Read;     // null: reflection
            internal static readonly double BuildMs;                    // harness: what deciding cost

            static HostBody()
            {
                Read = Build(out BuildMs);
            }

            static Func<MethodBase, byte[]> Build(out double ms)
            {
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                Func<MethodBase, byte[]> read = null;
                try
                {
                    read = Emit();
                    if (read != null)
                    {
                        MethodBase own = MethodBase.GetCurrentMethod();
                        byte[] host = read(own);
                        byte[] reflected = own.GetMethodBody().GetILAsByteArray();
                        if (host == null || !((ReadOnlySpan<byte>)host).SequenceEqual(reflected))
                        {
                            read = null;
                        }
                    }
                }
                catch
                {
                    read = null;
                }
                ms = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                return read;
            }

            static Func<MethodBase, byte[]> Emit()
            {
                Type updater = typeof(object).Assembly.GetType("WaylithClr.MethodBodyUpdater", false);
                if (updater == null)
                {
                    return null;
                }
                PropertyInfo supported = updater.GetProperty("IsSupported", BindingFlags.Public | BindingFlags.Static);
                if (supported != null && supported.PropertyType == typeof(bool) && !(bool)supported.GetValue(null))
                {
                    return null;
                }
                MethodInfo toArray = typeof(ReadOnlySpan<byte>).GetMethod("ToArray", Type.EmptyTypes);
                foreach (MethodInfo m in updater.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "TryGetDefaultMethodBody" || m.ReturnType != typeof(bool))
                    {
                        continue;
                    }
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length != 3 || ps[0].ParameterType != typeof(MethodBase) || !ps[2].ParameterType.IsByRef)
                    {
                        continue;
                    }
                    Type clauses = ps[1].ParameterType;                     // Span<MethodBodyClause>
                    Type view = ps[2].ParameterType.GetElementType();       // MethodBodyView
                    MethodInfo il = view.GetProperty("IL", BindingFlags.Public | BindingFlags.Instance)?.GetGetMethod();
                    if (!clauses.IsGenericType || clauses.GetGenericTypeDefinition() != typeof(Span<>) || !view.IsValueType
                        || il == null || il.ReturnType != typeof(ReadOnlySpan<byte>) || toArray == null)
                    {
                        continue;
                    }
                    // byte[] (MethodBase method) { Span<..> none = default; View body; ReadOnlySpan<byte> il;
                    //     if (!TryGetDefaultMethodBody(method, none, out body)) return null; il = body.IL; return il.ToArray(); }
                    DynamicMethod dm = new DynamicMethod("IsoRim_CompiledIL", typeof(byte[]), new[] { typeof(MethodBase) }, typeof(CopyWatch).Module, true);
                    ILGenerator g = dm.GetILGenerator();
                    LocalBuilder none = g.DeclareLocal(clauses);
                    LocalBuilder body = g.DeclareLocal(view);
                    LocalBuilder span = g.DeclareLocal(typeof(ReadOnlySpan<byte>));
                    Label nothing = g.DefineLabel();
                    g.Emit(OpCodes.Ldloca, none);
                    g.Emit(OpCodes.Initobj, clauses);
                    g.Emit(OpCodes.Ldarg_0);
                    g.Emit(OpCodes.Ldloc, none);
                    g.Emit(OpCodes.Ldloca, body);
                    g.Emit(OpCodes.Call, m);
                    g.Emit(OpCodes.Brfalse, nothing);
                    g.Emit(OpCodes.Ldloca, body);
                    g.Emit(OpCodes.Call, il);
                    g.Emit(OpCodes.Stloc, span);
                    g.Emit(OpCodes.Ldloca, span);
                    g.Emit(OpCodes.Call, toArray);
                    g.Emit(OpCodes.Ret);
                    g.MarkLabel(nothing);
                    g.Emit(OpCodes.Ldnull);
                    g.Emit(OpCodes.Ret);
                    return (Func<MethodBase, byte[]>)dm.CreateDelegate(typeof(Func<MethodBase, byte[]>));
                }
                return null;
            }
        }

        // Harness (ProbeWatchCopy): reflection only, as on a host without the runtime's reader; whether the runtime's reader
        // is in use; what deciding that cost; how the bodies were read so far.
        internal static bool ReflectionOnly;
        internal static bool HasHostReader => HostBody.Read != null;
        internal static double HostReaderMs => HostBody.BuildMs;
        internal static int HostReads, ReflectionReads;

        // THE reading of a method's IL for the hash (Api.CopyHash, every watch, the debug action): as compiled where the
        // runtime gives that (HostBody), else what reflection gives. Null: no IL body. Whether there is one is reflection's
        // answer, as it always was: for some extern methods ([DllImport]) the runtime has IL of its own making where
        // reflection has none (7 of the game's and the engine's 99,219 methods, measured 2026-11-18).
        static byte[] CompiledIL(MethodBase method)
        {
            MethodBody reflected = method.GetMethodBody();
            if (reflected == null)
            {
                return null;
            }
            if (!ReflectionOnly)
            {
                try
                {
                    byte[] il = HostBody.Read?.Invoke(method);
                    if (il != null && il.Length > 0)
                    {
                        HostReads++;
                        return il;
                    }
                }
                catch
                {
                }
            }
            ReflectionReads++;
            return reflected.GetILAsByteArray();
        }

        internal static string Hash(MethodBase method, out string error)
        {
            error = null;
            if (method == null)
            {
                error = "null";
                return null;
            }
            try
            {
                byte[] il = CompiledIL(method);
                if (il == null)
                {
                    error = "no IL body";
                    return null;
                }
                string text = Canonical(method, il);
                byte[] sum;
                using (SHA256 sha = SHA256.Create())
                {
                    sum = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                }
                StringBuilder hex = new StringBuilder(16);
                for (int i = 0; i < 8; i++)
                {
                    hex.Append(sum[i].ToString("x2"));
                }
                return hex.ToString();
            }
            catch (Exception e)
            {
                error = e.GetType().Name + ": " + e.Message;
                return null;
            }
        }

        static string Canonical(MethodBase method, byte[] il)
        {
            Module module = method.Module;
            Type owner = method.DeclaringType;
            Type[] typeArgs = owner != null && owner.IsGenericType ? owner.GetGenericArguments() : null;
            Type[] methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;
            StringBuilder sb = new StringBuilder(il.Length * 4);
            int pos = 0;
            while (pos < il.Length)
            {
                OpCode op;
                byte b = il[pos++];
                if (b == 0xfe)
                {
                    if (pos >= il.Length)
                    {
                        throw new InvalidOperationException("IL ends inside an opcode");
                    }
                    byte c = il[pos++];
                    op = OpTable.Two[c];
                    sb.Append("fe").Append(c.ToString("x2"));
                }
                else
                {
                    op = OpTable.One[b];
                    sb.Append(b.ToString("x2"));
                }
                if (op.Size == 0)
                {
                    throw new InvalidOperationException("unknown opcode at " + (pos - 1));
                }
                switch (op.OperandType)
                {
                    case OperandType.InlineNone:
                        break;
                    case OperandType.ShortInlineBrTarget:
                    case OperandType.ShortInlineI:
                    case OperandType.ShortInlineVar:
                        Raw(sb, il, ref pos, 1);
                        break;
                    case OperandType.InlineVar:
                        Raw(sb, il, ref pos, 2);
                        break;
                    case OperandType.InlineBrTarget:
                    case OperandType.InlineI:
                    case OperandType.ShortInlineR:
                        Raw(sb, il, ref pos, 4);
                        break;
                    case OperandType.InlineI8:
                    case OperandType.InlineR:
                        Raw(sb, il, ref pos, 8);
                        break;
                    case OperandType.InlineSwitch:
                    {
                        int n = BitConverter.ToInt32(il, pos);
                        Raw(sb, il, ref pos, 4);
                        Raw(sb, il, ref pos, checked(n * 4));
                        break;
                    }
                    case OperandType.InlineString:
                    {
                        int token = BitConverter.ToInt32(il, pos);
                        pos += 4;
                        string s = module.ResolveString(token);
                        sb.Append(" \"").Append(s.Length).Append(':').Append(s).Append('"');
                        break;
                    }
                    case OperandType.InlineSig:
                        pos += 4;
                        sb.Append(" sig");
                        break;
                    case OperandType.InlineMethod:
                    case OperandType.InlineField:
                    case OperandType.InlineType:
                    case OperandType.InlineTok:
                    {
                        int token = BitConverter.ToInt32(il, pos);
                        pos += 4;
                        sb.Append(' ').Append(Member(module.ResolveMember(token, typeArgs, methodArgs)));
                        break;
                    }
                    default:
                        throw new InvalidOperationException("unknown operand type " + op.OperandType);
                }
                sb.Append('\n');
            }
            return sb.ToString();
        }

        static void Raw(StringBuilder sb, byte[] il, ref int pos, int count)
        {
            if (count < 0 || pos + count > il.Length)
            {
                throw new InvalidOperationException("IL ends inside an operand");
            }
            sb.Append(' ');
            for (int i = 0; i < count; i++)
            {
                sb.Append(il[pos + i].ToString("x2"));
            }
            pos += count;
        }

        static string Member(MemberInfo m)
        {
            switch (m)
            {
                case Type t:
                    return TypeName(t);
                case FieldInfo f:
                    return TypeName(f.DeclaringType) + "::" + Compiled(f.Name) + ":" + TypeName(f.FieldType);
                case MethodBase mb:
                {
                    StringBuilder sb = new StringBuilder();
                    sb.Append(TypeName(mb.DeclaringType)).Append("::").Append(Compiled(mb.Name));
                    if (mb.IsGenericMethod)
                    {
                        Args(sb, mb.GetGenericArguments());
                    }
                    sb.Append('(');
                    ParameterInfo[] ps = mb.GetParameters();
                    for (int i = 0; i < ps.Length; i++)
                    {
                        sb.Append(i == 0 ? "" : ",").Append(TypeName(ps[i].ParameterType));
                    }
                    sb.Append(')');
                    if (mb is MethodInfo mi)
                    {
                        sb.Append(':').Append(TypeName(mi.ReturnType));
                    }
                    return sb.ToString();
                }
                case null:
                    return "?";
                default:
                    return m.MemberType + ":" + Compiled(m.Name);
            }
        }

        static string TypeName(Type t)
        {
            if (t == null)
            {
                return "?";
            }
            if (t.IsGenericParameter)
            {
                return (t.DeclaringMethod != null ? "!!" : "!") + t.GenericParameterPosition;
            }
            if (t.IsArray)
            {
                return TypeName(t.GetElementType()) + "[" + new string(',', t.GetArrayRank() - 1) + "]";
            }
            if (t.IsByRef)
            {
                return TypeName(t.GetElementType()) + "&";
            }
            if (t.IsPointer)
            {
                return TypeName(t.GetElementType()) + "*";
            }
            if (t.IsGenericType && !t.IsGenericTypeDefinition)
            {
                StringBuilder sb = new StringBuilder(TypeName(t.GetGenericTypeDefinition()));
                Args(sb, t.GetGenericArguments());
                return sb.ToString();
            }
            if (t.IsNested)
            {
                return TypeName(t.DeclaringType) + "/" + Compiled(t.Name);
            }
            return string.IsNullOrEmpty(t.Namespace) ? Compiled(t.Name) : t.Namespace + "." + Compiled(t.Name);
        }

        static void Args(StringBuilder sb, Type[] args)
        {
            sb.Append('<');
            for (int i = 0; i < args.Length; i++)
            {
                sb.Append(i == 0 ? "" : ",").Append(TypeName(args[i]));
            }
            sb.Append('>');
        }

        // A compiler-made name ("<Foo>b__12_0", "<>c__DisplayClass12_0") with each digit run as "#": those numbers count the
        // class's methods and lambdas, and move when another method of the class is added.
        static string Compiled(string name)
        {
            if (name == null || name.IndexOf('<') < 0)
            {
                return name;
            }
            StringBuilder sb = new StringBuilder(name.Length);
            bool digits = false;
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (c >= '0' && c <= '9')
                {
                    if (!digits)
                    {
                        sb.Append('#');
                    }
                    digits = true;
                    continue;
                }
                digits = false;
                sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
