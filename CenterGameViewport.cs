using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class CenterGameViewport
{
    private const string StoreTemplateHash = "2B42848BD6A8119A3FF26F80B9534D0D09115BADE521FCAE1515400751560BA0";
    private const string StoreEngineHash = "60E50F5336A56A6388294C23A2588A1A4D397BF5900B624CD50803C62FF66F56";
    private const double GameAspect = 3.0 / 4.0;
    private const int CenterAlignment = 1;

    private static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: CenterGameViewport <original game directory> <patch output directory>");
            return 2;
        }

        using (var templateSource = OpenOriginal(Path.Combine(args[0], "Template.exe"), StoreTemplateHash))
        using (var engineSource = OpenOriginal(Path.Combine(args[0], "UnityEngine.dll"), StoreEngineHash))
        using (var template = ModuleDefinition.ReadModule(templateSource))
        using (var engine = ModuleDefinition.ReadModule(engineSource))
        {
            var configureInput = template.ImportReference(ViewportPointerPatch.Apply(engine));
            var page = template.GetType("Template.MainPage");
            var layout = AddViewportLayout(template, page, configureInput);
            UpdateResizeHandler(page, layout);
            KeepResizeHandlerAfterSplash(page, layout);

            Directory.CreateDirectory(args[1]);
            WritePatch(engine, Path.Combine(args[1], "UnityEngine.dll"));
            WritePatch(template, Path.Combine(args[1], "Template.exe"));
        }

        return 0;
    }

    private static MemoryStream OpenOriginal(string path, string expectedHash)
    {
        var contents = File.ReadAllBytes(path);
        using (var sha256 = SHA256.Create())
        {
            string hash = BitConverter.ToString(sha256.ComputeHash(contents)).Replace("-", "");
            if (hash != expectedHash)
            {
                throw new InvalidDataException(Path.GetFileName(path) + " must be from the original Microsoft Store 1.1.1.10 release.");
            }
        }

        return new MemoryStream(contents, false);
    }

    private static void WritePatch(ModuleDefinition module, string path)
    {
        using (var output = new FileStream(path, FileMode.CreateNew))
        {
            module.Write(output);
        }
    }

    private static MethodDefinition AddViewportLayout(
        ModuleDefinition module, TypeDefinition page, MethodReference configureInput)
    {
        var panel = page.Fields.Single(field => field.Name == "DXSwapChainPanel");
        var currentWindow = FindReference(module, "Windows.UI.Xaml.Window", "get_Current");
        var getWidth = FindReference(module, "Windows.Foundation.Rect", "get_Width");
        var getHeight = FindReference(module, "Windows.Foundation.Rect", "get_Height");
        var boundsType = page.Fields.Single(field => field.Name == "splashImageRect").FieldType;
        var getBounds = new MethodReference("get_Bounds", boundsType, currentWindow.DeclaringType)
        {
            HasThis = true
        };
        var setWidth = FindReference(module, "Windows.UI.Xaml.FrameworkElement", "put_Width");
        var setHeight = FindReference(module, "Windows.UI.Xaml.FrameworkElement", "put_Height");
        var setHorizontalAlignment = AlignmentSetter(module, setWidth.DeclaringType, "HorizontalAlignment");
        var setVerticalAlignment = AlignmentSetter(module, setWidth.DeclaringType, "VerticalAlignment");

        var layout = new MethodDefinition("LayoutGameViewport",
            MethodAttributes.Private | MethodAttributes.HideBySig, module.TypeSystem.Void);
        page.Methods.Add(layout);
        var bounds = new VariableDefinition(getBounds.ReturnType);
        var width = new VariableDefinition(module.TypeSystem.Double);
        var height = new VariableDefinition(module.TypeSystem.Double);
        layout.Body.Variables.Add(bounds);
        layout.Body.Variables.Add(width);
        layout.Body.Variables.Add(height);
        layout.Body.InitLocals = true;

        var il = layout.Body.GetILProcessor();
        var fitByWidth = Instruction.Create(OpCodes.Ldloc, width);
        var apply = Instruction.Create(OpCodes.Ldarg_0);
        var done = Instruction.Create(OpCodes.Ret);

        il.Emit(OpCodes.Call, currentWindow);
        il.Emit(OpCodes.Callvirt, getBounds);
        il.Emit(OpCodes.Stloc, bounds);
        il.Emit(OpCodes.Ldloca, bounds);
        il.Emit(OpCodes.Call, getWidth);
        il.Emit(OpCodes.Stloc, width);
        il.Emit(OpCodes.Ldloca, bounds);
        il.Emit(OpCodes.Call, getHeight);
        il.Emit(OpCodes.Stloc, height);

        // The view can briefly report zero dimensions during a mode change.
        il.Emit(OpCodes.Ldloc, width);
        il.Emit(OpCodes.Ldc_R8, 0.0);
        il.Emit(OpCodes.Ble_Un, done);
        il.Emit(OpCodes.Ldloc, height);
        il.Emit(OpCodes.Ldc_R8, 0.0);
        il.Emit(OpCodes.Ble_Un, done);

        // Fit the same portrait aspect used by the Unity renderer inside the window.
        il.Emit(OpCodes.Ldloc, width);
        il.Emit(OpCodes.Ldloc, height);
        il.Emit(OpCodes.Ldc_R8, GameAspect);
        il.Emit(OpCodes.Mul);
        il.Emit(OpCodes.Ble, fitByWidth);
        il.Emit(OpCodes.Ldloc, height);
        il.Emit(OpCodes.Ldc_R8, GameAspect);
        il.Emit(OpCodes.Mul);
        il.Emit(OpCodes.Stloc, width);
        il.Emit(OpCodes.Br, apply);
        il.Append(fitByWidth);
        il.Emit(OpCodes.Ldc_R8, GameAspect);
        il.Emit(OpCodes.Div);
        il.Emit(OpCodes.Stloc, height);

        il.Append(apply);
        il.Emit(OpCodes.Ldfld, panel);
        il.Emit(OpCodes.Ldc_I4, CenterAlignment);
        il.Emit(OpCodes.Callvirt, setHorizontalAlignment);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, panel);
        il.Emit(OpCodes.Ldc_I4, CenterAlignment);
        il.Emit(OpCodes.Callvirt, setVerticalAlignment);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, panel);
        il.Emit(OpCodes.Ldloc, width);
        il.Emit(OpCodes.Callvirt, setWidth);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, panel);
        il.Emit(OpCodes.Ldloc, height);
        il.Emit(OpCodes.Callvirt, setHeight);

        // Publish the same margins used for rendering in units of the visible viewport.
        EmitRelativeMargin(il, bounds, getWidth, width);
        EmitRelativeMargin(il, bounds, getHeight, height);
        il.Emit(OpCodes.Call, configureInput);
        il.Append(done);
        return layout;
    }

    private static void EmitRelativeMargin(
        ILProcessor il, VariableDefinition bounds, MethodReference size, VariableDefinition visibleSize)
    {
        il.Emit(OpCodes.Ldloca, bounds);
        il.Emit(OpCodes.Call, size);
        il.Emit(OpCodes.Ldloc, visibleSize);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Ldc_R8, 0.5);
        il.Emit(OpCodes.Mul);
        il.Emit(OpCodes.Ldloc, visibleSize);
        il.Emit(OpCodes.Div);
        il.Emit(OpCodes.Conv_R4);
    }

    private static void UpdateResizeHandler(TypeDefinition page, MethodDefinition layout)
    {
        var resize = page.Methods.Single(method => method.Name == "OnResize");
        var splashMissing = resize.Body.Instructions.Single(op => op.OpCode == OpCodes.Brfalse_S);
        var startLayout = Instruction.Create(OpCodes.Ldarg_0);
        var il = resize.Body.GetILProcessor();
        splashMissing.OpCode = OpCodes.Brfalse;
        splashMissing.Operand = startLayout;
        il.Append(startLayout);
        il.Emit(OpCodes.Call, layout);
        il.Emit(OpCodes.Ret);
    }

    private static void KeepResizeHandlerAfterSplash(TypeDefinition page, MethodDefinition layout)
    {
        var removeSplash = page.Methods.Single(method => method.Name == "RemoveSplashScreen");
        var splashRemoved = removeSplash.Body.Instructions.Single(op => op.OpCode == OpCodes.Pop);
        var splash = page.Fields.Single(field => field.Name == "splash");
        var il = removeSplash.Body.GetILProcessor();

        // The original cleanup unregisters the window size event after removing the image.
        // Keep the event for the life of the game page and clear the splash reference.
        while (splashRemoved.Next != null)
        {
            il.Remove(splashRemoved.Next);
        }

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Stfld, splash);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, layout);
        il.Emit(OpCodes.Ret);
    }

    private static MethodReference FindReference(ModuleDefinition module, string type, string name)
    {
        return module.GetMemberReferences().OfType<MethodReference>()
            .Single(reference => reference.DeclaringType.FullName == type && reference.Name == name);
    }

    private static MethodReference AlignmentSetter(ModuleDefinition module, TypeReference element, string name)
    {
        var alignment = new TypeReference("Windows.UI.Xaml", name, module, element.Scope, true);
        var method = new MethodReference("put_" + name, module.TypeSystem.Void, element) { HasThis = true };
        method.Parameters.Add(new ParameterDefinition(alignment));
        return method;
    }
}
