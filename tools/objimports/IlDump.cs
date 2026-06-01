using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

// Disassemble a method body and flag structural anomalies that cause the JIT to
// throw InvalidProgramException: operand tokens with row 0, branch targets outside
// the method, unknown opcodes, switch tables out of range.
static class IlDump
{
    static readonly Dictionary<short, OpCode> Ops = Build();
    static Dictionary<short, OpCode> Build()
    {
        var d = new Dictionary<short, OpCode>();
        foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (f.GetValue(null) is OpCode op) d[op.Value] = op;
        return d;
    }

    public static void Dump(string file, string method, bool full)
    {
        using var fs = File.OpenRead(file);
        using var pe = new PEReader(fs);
        var r = pe.GetMetadataReader();
        foreach (var mh in r.MethodDefinitions)
        {
            var md = r.GetMethodDefinition(mh);
            if (r.GetString(md.Name) != method) continue;
            if (md.RelativeVirtualAddress == 0) { Console.WriteLine($"{method}: no body (pinvoke/abstract)"); return; }
            var body = pe.GetMethodBody(md.RelativeVirtualAddress);
            byte[] il = body.GetILBytes();
            int methodCount = r.GetTableRowCount(TableIndex.MethodDef);
            int fieldCount = r.GetTableRowCount(TableIndex.Field);
            int memberRefCount = r.GetTableRowCount(TableIndex.MemberRef);
            int saSigCount = r.GetTableRowCount(TableIndex.StandAloneSig);
            Console.WriteLine($"{method}: maxstack={body.MaxStack} codesize={il.Length} localsig=0x{(body.LocalSignature.IsNil ? 0 : MetadataTokens.GetToken(body.LocalSignature)):X8}");

            int anomalies = 0, pos = 0;
            while (pos < il.Length)
            {
                int start = pos;
                short code = il[pos++];
                if (code == 0xFE && pos < il.Length) code = (short)(0xFE00 | il[pos++]);
                if (!Ops.TryGetValue(code, out var op)) { Console.WriteLine($"  !! IL_{start:X4}: UNKNOWN opcode 0x{code:X}"); anomalies++; break; }
                string note = "";
                bool bad = false;
                switch (op.OperandType)
                {
                    case OperandType.InlineNone: break;
                    case OperandType.ShortInlineI: case OperandType.ShortInlineVar: pos += 1; break;
                    case OperandType.ShortInlineBrTarget: { sbyte dd = (sbyte)il[pos]; pos += 1; int tgt = pos + dd; if (tgt < 0 || tgt > il.Length) { bad = true; note = $" BR-OOR -> {tgt}"; } break; }
                    case OperandType.InlineVar: pos += 2; break;
                    case OperandType.InlineBrTarget: { int dd = BitConverter.ToInt32(il, pos); pos += 4; int tgt = pos + dd; if (tgt < 0 || tgt > il.Length) { bad = true; note = $" BR-OOR -> {tgt}"; } break; }
                    case OperandType.ShortInlineR: pos += 4; break;
                    case OperandType.InlineI: pos += 4; break;
                    case OperandType.InlineI8: case OperandType.InlineR: pos += 8; break;
                    case OperandType.InlineString:
                    case OperandType.InlineMethod: case OperandType.InlineField: case OperandType.InlineTok:
                    case OperandType.InlineType: case OperandType.InlineSig:
                    {
                        int tok = BitConverter.ToInt32(il, pos); pos += 4;
                        int table = (int)((uint)tok >> 24), row = tok & 0xFFFFFF;
                        note = $" tok=0x{tok:X8} {Resolve(r, tok)}";
                        // row 0, or row beyond the table => invalid token reference
                        if (row == 0) { bad = true; note += " <ROW-0!>"; }
                        else if (table == 0x06 && row > methodCount) { bad = true; note += " <METHOD-OOR!>"; }
                        else if (table == 0x04 && row > fieldCount) { bad = true; note += " <FIELD-OOR!>"; }
                        else if (table == 0x0A && row > memberRefCount) { bad = true; note += " <MEMBERREF-OOR!>"; }
                        else if (table == 0x11 && row > saSigCount) { bad = true; note += " <SIG-OOR!>"; }
                        break;
                    }
                    case OperandType.InlineSwitch: { int n = BitConverter.ToInt32(il, pos); pos += 4 + 4 * n; note = $" switch[{n}]"; break; }
                }
                if (bad) { Console.WriteLine($"  !! IL_{start:X4}: {op.Name}{note}"); anomalies++; }
                else if (full) Console.WriteLine($"  IL_{start:X4}: {op.Name}{note}");
            }
            Console.WriteLine($"  --- anomalies: {anomalies} ---");
            return;
        }
        Console.WriteLine($"{method}: not found");
    }

    // Abstract stack-depth check (worklist). Finds the two InvalidProgram causes
    // left after tokens/branches are clean: depth > maxstack, and inconsistent depth
    // where two paths join (the JIT requires equal stack depth at every offset).
    public static void Check(string file, string method)
    {
        using var fs = File.OpenRead(file);
        using var pe = new PEReader(fs);
        var r = pe.GetMetadataReader();
        foreach (var mh in r.MethodDefinitions)
        {
            var md = r.GetMethodDefinition(mh);
            if (r.GetString(md.Name) != method) continue;
            if (md.RelativeVirtualAddress == 0) { Console.WriteLine($"{method}: no body"); return; }
            var body = pe.GetMethodBody(md.RelativeVirtualAddress);
            byte[] il = body.GetILBytes();
            int max = body.MaxStack;
            var depth = new int[il.Length + 1]; for (int i = 0; i <= il.Length; i++) depth[i] = int.MinValue;
            var work = new Stack<int>(); depth[0] = 0; work.Push(0);
            int issues = 0;
            void Seen(int from, string opn, int tgt, int dd)
            {
                if (tgt < 0 || tgt > il.Length) { Console.WriteLine($"  !! IL_{from:X4} {opn}: target OOR {tgt}"); issues++; return; }
                if (depth[tgt] == int.MinValue) { depth[tgt] = dd; work.Push(tgt); }
                else if (depth[tgt] != dd) { Console.WriteLine($"  !! IL_{from:X4} {opn}: JOIN IMBALANCE at IL_{tgt:X4} (have {depth[tgt]}, got {dd})"); issues++; }
            }
            while (work.Count > 0)
            {
                int start = work.Pop(); int d = depth[start]; int pos = start;
                short code = il[pos++];
                if (code == 0xFE && pos < il.Length) code = (short)(0xFE00 | il[pos++]);
                if (!Ops.TryGetValue(code, out var op)) { Console.WriteLine($"  !! IL_{start:X4} unknown opcode"); issues++; continue; }
                int operandLen = OperandLen(op.OperandType, il, pos, out int brTarget, out int[] switchTargets);
                int pop, push;
                if (op.Value == 0x28 || op.Value == 0x6F || op.Value == 0x73)
                    CallEffect(r, BitConverter.ToInt32(il, pos), op.Value == 0x73, out pop, out push);
                else if (op.Value == 0x29) CalliEffect(r, BitConverter.ToInt32(il, pos), out pop, out push);
                else if (op.Value == 0x2A) { pop = MethodReturnsValue(r, md) ? 1 : 0; push = 0; }
                else { pop = PopCount(op.StackBehaviourPop); push = PushCount(op.StackBehaviourPush); }
                d -= pop;
                if (d < 0) { Console.WriteLine($"  !! IL_{start:X4} {op.Name}: stack UNDERFLOW ({d})"); issues++; d = 0; }
                d += push;
                if (d > max) { Console.WriteLine($"  !! IL_{start:X4} {op.Name}: depth {d} > maxstack {max}"); issues++; }
                int next = pos + operandLen;
                var fc = op.FlowControl;
                if (op.OperandType == OperandType.InlineSwitch) { foreach (var t in switchTargets) Seen(start, op.Name, t, d); Seen(start, op.Name, next, d); }
                else if (fc == FlowControl.Branch) Seen(start, op.Name, brTarget, d);
                else if (fc == FlowControl.Cond_Branch) { Seen(start, op.Name, brTarget, d); Seen(start, op.Name, next, d); }
                else if (fc == FlowControl.Return || fc == FlowControl.Throw) { }
                else Seen(start, op.Name, next, d); // fall-through
            }
            Console.WriteLine($"{method}: maxstack={max} codesize={il.Length} -> stack issues: {issues}");
            return;
        }
    }

    static int OperandLen(OperandType t, byte[] il, int pos, out int brTarget, out int[] sw)
    {
        brTarget = -1; sw = System.Array.Empty<int>();
        switch (t)
        {
            case OperandType.InlineNone: return 0;
            case OperandType.ShortInlineI: case OperandType.ShortInlineVar: return 1;
            case OperandType.ShortInlineBrTarget: brTarget = pos + 1 + (sbyte)il[pos]; return 1;
            case OperandType.InlineVar: return 2;
            case OperandType.InlineBrTarget: brTarget = pos + 4 + BitConverter.ToInt32(il, pos); return 4;
            case OperandType.ShortInlineR: case OperandType.InlineI:
            case OperandType.InlineString: case OperandType.InlineMethod: case OperandType.InlineField:
            case OperandType.InlineTok: case OperandType.InlineType: case OperandType.InlineSig: return 4;
            case OperandType.InlineI8: case OperandType.InlineR: return 8;
            case OperandType.InlineSwitch:
                int n = BitConverter.ToInt32(il, pos); int baseAfter = pos + 4 + 4 * n; sw = new int[n];
                for (int i = 0; i < n; i++) sw[i] = baseAfter + BitConverter.ToInt32(il, pos + 4 + 4 * i);
                return 4 + 4 * n;
            default: return 0;
        }
    }

    static int PopCount(StackBehaviour b) => b switch
    {
        StackBehaviour.Pop0 => 0, StackBehaviour.Varpop => 0,
        StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref => 1,
        StackBehaviour.Pop1_pop1 or StackBehaviour.Popi_pop1 or StackBehaviour.Popi_popi or StackBehaviour.Popi_popi8
            or StackBehaviour.Popi_popr4 or StackBehaviour.Popi_popr8 or StackBehaviour.Popref_pop1 or StackBehaviour.Popref_popi => 2,
        StackBehaviour.Popi_popi_popi or StackBehaviour.Popref_popi_popi or StackBehaviour.Popref_popi_popi8
            or StackBehaviour.Popref_popi_popr4 or StackBehaviour.Popref_popi_popr8 or StackBehaviour.Popref_popi_popref => 3,
        _ => 0,
    };
    static int PushCount(StackBehaviour b) => b switch
    {
        StackBehaviour.Push0 or StackBehaviour.Varpush => 0,
        StackBehaviour.Push1 or StackBehaviour.Pushi or StackBehaviour.Pushi8 or StackBehaviour.Pushr4 or StackBehaviour.Pushref => 1,
        StackBehaviour.Push1_push1 => 2,
        _ => 0,
    };

    static void CallEffect(MetadataReader r, int tok, bool newobj, out int pop, out int push)
    {
        pop = 0; push = newobj ? 1 : 0;
        BlobReader sig; bool hasThis = false; int table = (int)((uint)tok >> 24), row = tok & 0xFFFFFF;
        if (table == 0x06) { var m = r.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(row)); sig = r.GetBlobReader(m.Signature); }
        else if (table == 0x0A) { var m = r.GetMemberReference(MetadataTokens.MemberReferenceHandle(row)); sig = r.GetBlobReader(m.Signature); }
        else return;
        var h = sig.ReadSignatureHeader(); hasThis = h.IsInstance; if (h.IsGeneric) sig.ReadCompressedInteger();
        int pc = sig.ReadCompressedInteger();
        pop = pc + (hasThis && !newobj ? 1 : 0);
        if (!newobj) push = PeekVoid(ref sig) ? 0 : 1;
    }
    static void CalliEffect(MetadataReader r, int tok, out int pop, out int push)
    {
        pop = 1; push = 0; // the function pointer itself
        var ss = r.GetStandaloneSignature(MetadataTokens.StandaloneSignatureHandle(tok & 0xFFFFFF));
        var sig = r.GetBlobReader(ss.Signature);
        var h = sig.ReadSignatureHeader(); if (h.IsGeneric) sig.ReadCompressedInteger();
        int pc = sig.ReadCompressedInteger(); pop += pc + (h.IsInstance ? 1 : 0);
        push = PeekVoid(ref sig) ? 0 : 1;
    }
    static bool PeekVoid(ref BlobReader sig)
    {
        int o = sig.Offset;
        byte b = sig.ReadByte();
        while (b == 0x1F || b == 0x20) { sig.ReadCompressedInteger(); b = sig.ReadByte(); } // skip CMOD_REQD/OPT
        sig.Offset = o;
        return b == 0x01; // ELEMENT_TYPE_VOID
    }
    static bool MethodReturnsValue(MetadataReader r, MethodDefinition md)
    {
        var sig = r.GetBlobReader(md.Signature); var h = sig.ReadSignatureHeader();
        if (h.IsGeneric) sig.ReadCompressedInteger(); sig.ReadCompressedInteger(); return !PeekVoid(ref sig);
    }

    static string Resolve(MetadataReader r, int tok)
    {
        try
        {
            int table = (int)((uint)tok >> 24), row = tok & 0xFFFFFF;
            if (row == 0) return "";
            if (table == 0x06) return r.GetString(r.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(row)).Name);
            if (table == 0x0A) return r.GetString(r.GetMemberReference(MetadataTokens.MemberReferenceHandle(row)).Name);
            if (table == 0x04) return r.GetString(r.GetFieldDefinition(MetadataTokens.FieldDefinitionHandle(row)).Name);
            if (table == 0x70) return "\"" + r.GetUserString(MetadataTokens.UserStringHandle(row)) + "\"";
        }
        catch { }
        return "";
    }
}
