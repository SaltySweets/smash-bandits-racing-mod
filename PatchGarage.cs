using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class PatchGarage
{
    private const string OriginalAssemblyHash = "72E4E56D8C167FBCBBD7653CA954BAAC19737A2ECCB583F2EBBE943FCEA2ED74";

    private static int Main(string[] args)
    {
        if (args.Length != 3)
        {
            Console.Error.WriteLine("Usage: PatchGarage <original Game directory> <compiled GarageViewport.dll> <output directory>");
            return 2;
        }

        string original = Path.GetFullPath(Path.Combine(args[0], "Assembly-CSharp.dll"));
        string outputFile = Path.GetFullPath(Path.Combine(args[2], "Assembly-CSharp.dll"));
        if (String.Equals(original, outputFile, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose a separate output directory.");

        using (var sha256 = SHA256.Create())
        using (var input = File.OpenRead(original))
        {
            if (BitConverter.ToString(sha256.ComputeHash(input)).Replace("-", "") != OriginalAssemblyHash)
                throw new InvalidOperationException("Unsupported original Assembly-CSharp.dll.");
        }

        using (var resolver = new DefaultAssemblyResolver())
        {
            resolver.AddSearchDirectory(Path.GetDirectoryName(original));
            using (var game = ModuleDefinition.ReadModule(original, new ReaderParameters { AssemblyResolver = resolver }))
            using (var payload = ModuleDefinition.ReadModule(args[1]))
            {
                var viewport = new ViewportAssembly(game, payload.GetType("GarageViewport")).Add();
                PatchPanel(game.GetType("UICarSelectPanel"), viewport);
                PatchPageResize(game, Method(viewport, "PreserveFrontEndPage"));
                Directory.CreateDirectory(Path.GetDirectoryName(outputFile));
                game.Write(outputFile);
            }
        }

        Console.WriteLine(outputFile);
        return 0;
    }

    private static void PatchPanel(TypeDefinition panel, TypeDefinition viewport)
    {
        var clip = panel.Fields.Single(f => f.Name == "m_scrollyClipPanel");
        var viewportField = new FieldDefinition("m_garageViewport", FieldAttributes.Private, viewport);
        panel.Fields.Add(viewportField);
        var swipeHint = panel.BaseType.Resolve().Fields.Single(f => f.Name == "m_swipeHint");

        var start = Method(panel, "Start");
        var startIL = start.Body.GetILProcessor();
        var afterClip = start.Body.Instructions.Single(i => i.OpCode == OpCodes.Stfld && i.Operand == clip).Next;
        startIL.InsertBefore(afterClip, startIL.Create(OpCodes.Ldarg_0));
        foreach (var field in new[] { clip, panel.Fields.Single(f => f.Name == "m_centerOnChild"),
            panel.Fields.Single(f => f.Name == "m_uiCamera"), panel.Fields.Single(f => f.Name == "m_downButton"), swipeHint })
        {
            startIL.InsertBefore(afterClip, startIL.Create(OpCodes.Ldarg_0));
            startIL.InsertBefore(afterClip, startIL.Create(OpCodes.Ldfld, field));
        }
        startIL.InsertBefore(afterClip, startIL.Create(OpCodes.Newobj, Method(viewport, ".ctor")));
        startIL.InsertBefore(afterClip, startIL.Create(OpCodes.Stfld, viewportField));

        var reset = Method(panel, "HardResetVehicleList");
        var afterReset = reset.Body.Instructions.Single(i => IsCall(i, "UIPanel", "set_clipRange")).Next;
        var updateClip = Method(viewport, "UpdateClip");
        InsertClipUpdate(reset, afterReset, viewportField, updateClip);
        var update = Method(panel, "Update");
        InsertClipUpdate(update, update.Body.Instructions[0], viewportField, updateClip);

        var loader = panel.NestedTypes.Single(t => t.Name.StartsWith("<LoadDisplayVehicle>", StringComparison.Ordinal));
        var moveNext = Method(loader, "MoveNext");
        var assignPreview = moveNext.Body.Instructions.Single(i => i.OpCode == OpCodes.Stfld
            && ((FieldReference)i.Operand).Name == "vehicleObject" && i.Previous.OpCode == OpCodes.Isinst
            && IsCall(i.Previous.Previous, "UnityEngine.Object", "Instantiate"));
        var loadIL = moveNext.Body.GetILProcessor();
        loadIL.InsertBefore(assignPreview, loadIL.Create(OpCodes.Call, Method(viewport, "PreparePreview")));

        var loseFocus = Method(panel, "OnFrontEndLostFocus");
        var first = loseFocus.Body.Instructions[0];
        var focusIL = loseFocus.Body.GetILProcessor();
        focusIL.InsertBefore(first, focusIL.Create(OpCodes.Ldarg_0));
        focusIL.InsertBefore(first, focusIL.Create(OpCodes.Call, Method(panel.BaseType.Resolve(), "OnFrontEndLostFocus")));
        GuardDelayedHint(panel, swipeHint);
    }

    private static void InsertClipUpdate(MethodDefinition method, Instruction before, FieldDefinition field,
        MethodDefinition updateClip)
    {
        var il = method.Body.GetILProcessor();
        var call = il.Create(OpCodes.Callvirt, updateClip);
        var absent = il.Create(OpCodes.Pop);
        il.InsertBefore(before, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(before, il.Create(OpCodes.Ldfld, field));
        il.InsertBefore(before, il.Create(OpCodes.Dup));
        il.InsertBefore(before, il.Create(OpCodes.Brfalse_S, absent));
        il.InsertBefore(before, call);
        il.InsertBefore(before, il.Create(OpCodes.Br_S, before));
        il.InsertBefore(before, absent);
    }

    private static void PatchPageResize(ModuleDefinition game, MethodDefinition preservePage)
    {
        var stretchGrid = game.GetType("UIStretchGrid");
        var resize = Method(stretchGrid, "DoStretch");
        var grid = stretchGrid.Fields.Single(f => f.Name == "mGrid");
        var width = game.GetType("UIGrid").Fields.Single(f => f.Name == "cellWidth");
        var previousWidth = new VariableDefinition(game.TypeSystem.Single);
        resize.Body.Variables.Add(previousWidth);

        var il = resize.Body.GetILProcessor();
        var beforeResize = resize.Body.Instructions.Single(i => IsCall(i, "UIStretchCustom", "CalculateStretch")).Next.Next;
        il.InsertBefore(beforeResize, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(beforeResize, il.Create(OpCodes.Ldfld, grid));
        il.InsertBefore(beforeResize, il.Create(OpCodes.Ldfld, width));
        il.InsertBefore(beforeResize, il.Create(OpCodes.Stloc, previousWidth));

        var afterResize = resize.Body.Instructions.Single(i => IsCall(i, "UIGrid", "Reposition")).Next;
        il.InsertBefore(afterResize, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(afterResize, il.Create(OpCodes.Ldfld, grid));
        il.InsertBefore(afterResize, il.Create(OpCodes.Ldloc, previousWidth));
        il.InsertBefore(afterResize, il.Create(OpCodes.Call, preservePage));
    }

    private static void GuardDelayedHint(TypeDefinition garage, FieldDefinition swipeHint)
    {
        var basePanel = garage.BaseType.Resolve();
        var iterator = basePanel.NestedTypes.Single(t => t.Name.StartsWith("<DelayedShowSwipeHint>", StringComparison.Ordinal));
        var resume = Method(iterator, "MoveNext");
        var owner = iterator.Fields.Single(f => f.Name == "<>4__this");
        var allow = basePanel.Fields.Single(f => f.Name == "m_bAllowSwipeHints");
        var activate = resume.Body.Instructions.Single(i => i.OpCode == OpCodes.Ldfld && i.Operand == swipeHint).Previous.Previous;
        var returnFalse = resume.Body.Instructions[resume.Body.Instructions.Count - 2];
        foreach (var instruction in resume.Body.Instructions.Where(i => i.OpCode == OpCodes.Br_S && i.Operand == returnFalse))
            instruction.OpCode = OpCodes.Br;

        var il = resume.Body.GetILProcessor();
        il.InsertBefore(activate, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(activate, il.Create(OpCodes.Ldfld, owner));
        il.InsertBefore(activate, il.Create(OpCodes.Isinst, garage));
        il.InsertBefore(activate, il.Create(OpCodes.Brfalse_S, activate));
        il.InsertBefore(activate, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(activate, il.Create(OpCodes.Ldfld, owner));
        il.InsertBefore(activate, il.Create(OpCodes.Callvirt, Method(basePanel, "get_hasFocus")));
        il.InsertBefore(activate, il.Create(OpCodes.Brfalse_S, returnFalse));
        il.InsertBefore(activate, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(activate, il.Create(OpCodes.Ldfld, owner));
        il.InsertBefore(activate, il.Create(OpCodes.Ldfld, allow));
        il.InsertBefore(activate, il.Create(OpCodes.Brfalse_S, returnFalse));
    }

    private static bool IsCall(Instruction instruction, string declaringType, string name)
    {
        var method = instruction.Operand as MethodReference;
        return (instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt)
            && method != null && method.DeclaringType.FullName == declaringType && method.Name == name;
    }

    private static MethodDefinition Method(TypeDefinition type, string name)
    {
        return type.Methods.Single(method => method.Name == name);
    }

    // Import only the helper class so the Store package needs no extra assembly.
    private sealed class ViewportAssembly
    {
        private readonly ModuleDefinition game;
        private readonly TypeDefinition source;
        private readonly TypeDefinition viewport;
        private readonly Dictionary<FieldDefinition, FieldDefinition> fields = new Dictionary<FieldDefinition, FieldDefinition>();
        private readonly Dictionary<MethodDefinition, MethodDefinition> methods = new Dictionary<MethodDefinition, MethodDefinition>();

        public ViewportAssembly(ModuleDefinition game, TypeDefinition source)
        {
            this.game = game;
            this.source = source;
            viewport = new TypeDefinition(source.Namespace, source.Name, source.Attributes, game.TypeSystem.Object);
        }

        public TypeDefinition Add()
        {
            game.Types.Add(viewport);
            foreach (var field in source.Fields)
            {
                var copy = new FieldDefinition(field.Name, field.Attributes, ImportType(field.FieldType));
                viewport.Fields.Add(copy);
                fields.Add(field, copy);
            }
            foreach (var method in source.Methods)
            {
                var copy = new MethodDefinition(method.Name, method.Attributes, ImportType(method.ReturnType));
                foreach (var parameter in method.Parameters)
                    copy.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes, ImportType(parameter.ParameterType)));
                viewport.Methods.Add(copy);
                methods.Add(method, copy);
            }
            foreach (var method in source.Methods)
                CopyBody(method, methods[method]);
            return viewport;
        }

        private void CopyBody(MethodDefinition from, MethodDefinition to)
        {
            if (from.Body.HasExceptionHandlers)
                throw new InvalidOperationException("The garage helper uses unsupported exception handlers.");
            to.Body.InitLocals = from.Body.InitLocals;
            foreach (var variable in from.Body.Variables)
                to.Body.Variables.Add(new VariableDefinition(ImportType(variable.VariableType)));

            var instructions = new Dictionary<Instruction, Instruction>();
            var il = to.Body.GetILProcessor();
            foreach (var original in from.Body.Instructions)
            {
                var copy = il.Create(OpCodes.Nop);
                copy.OpCode = original.OpCode;
                il.Append(copy);
                instructions.Add(original, copy);
            }
            foreach (var original in from.Body.Instructions)
            {
                object operand = original.Operand;
                if (operand is Instruction) operand = instructions[(Instruction)operand];
                else if (operand is Instruction[]) operand = ((Instruction[])operand).Select(i => instructions[i]).ToArray();
                else if (operand is VariableDefinition) operand = to.Body.Variables[((VariableDefinition)operand).Index];
                else if (operand is ParameterDefinition) operand = to.Parameters[((ParameterDefinition)operand).Index];
                else if (operand is FieldReference) operand = ImportField((FieldReference)operand);
                else if (operand is MethodReference) operand = ImportMethod((MethodReference)operand);
                else if (operand is TypeReference) operand = ImportType((TypeReference)operand);
                instructions[original].Operand = operand;
            }
        }

        private bool IsGameType(TypeReference type)
        {
            var assembly = type.Scope as AssemblyNameReference;
            return assembly != null && assembly.Name == game.Assembly.Name.Name;
        }

        private TypeReference ImportType(TypeReference type)
        {
            if (type == source) return viewport;
            if (IsGameType(type)) return game.GetType(type.FullName);
            return game.ImportReference(type);
        }

        private MethodReference ImportMethod(MethodReference method)
        {
            var generic = method as GenericInstanceMethod;
            if (generic != null)
            {
                var copy = new GenericInstanceMethod(ImportMethod(generic.ElementMethod));
                foreach (var type in generic.GenericArguments)
                    copy.GenericArguments.Add(ImportType(type));
                return copy;
            }
            if (method.DeclaringType == source) return methods[(MethodDefinition)method];
            if (IsGameType(method.DeclaringType))
                return ImportType(method.DeclaringType).Resolve().Methods.Single(m => m.FullName == method.FullName);
            return game.ImportReference(method);
        }

        private FieldReference ImportField(FieldReference field)
        {
            if (field.DeclaringType == source) return fields[(FieldDefinition)field];
            if (IsGameType(field.DeclaringType))
                return ImportType(field.DeclaringType).Resolve().Fields.Single(f => f.FullName == field.FullName);
            return game.ImportReference(field);
        }
    }
}
