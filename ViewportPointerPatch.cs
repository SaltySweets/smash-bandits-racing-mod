using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class ViewportPointerPatch
{
    internal static MethodDefinition Apply(ModuleDefinition engine)
    {
        var vector2 = engine.GetType("UnityEngine.Vector2");
        var vector3 = engine.GetType("UnityEngine.Vector3");
        var screen = engine.GetType("UnityEngine.Screen");
        var input = engine.GetType("UnityEngine.Input");
        var touch = engine.GetType("UnityEngine.Touch");
        var mapping = new TypeDefinition("UnityEngine", "CenteredViewportPointer",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
            engine.TypeSystem.Object);
        engine.Types.Add(mapping);

        // Publish both margins together between the XAML and Unity threads.
        var margins = new FieldDefinition("margins", FieldAttributes.Private | FieldAttributes.Static,
            engine.TypeSystem.Object);
        mapping.Fields.Add(margins);
        var configure = AddConfigurationMethod(engine, mapping, vector2, margins);
        var toMouse = AddTranslation(mapping, vector3, vector2, margins, screen);
        var toTouch = AddTranslation(mapping, vector2, vector2, margins, screen);
        TranslateGetter(input, "get_mousePosition", toMouse);
        TranslateGetter(touch, "get_position", toTouch);
        TranslateGetter(touch, "get_rawPosition", toTouch);
        return configure;
    }

    private static MethodDefinition AddConfigurationMethod(
        ModuleDefinition engine, TypeDefinition mapping, TypeDefinition vector2, FieldDefinition margins)
    {
        var constructor = vector2.Methods.Single(method => method.IsConstructor && method.Parameters.Count == 2);
        var configure = new MethodDefinition("SetRelativeMargins",
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, engine.TypeSystem.Void);
        configure.Parameters.Add(new ParameterDefinition("left", ParameterAttributes.None, engine.TypeSystem.Single));
        configure.Parameters.Add(new ParameterDefinition("top", ParameterAttributes.None, engine.TypeSystem.Single));
        mapping.Methods.Add(configure);
        var il = configure.Body.GetILProcessor();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Newobj, constructor);
        il.Emit(OpCodes.Box, vector2);
        il.Emit(OpCodes.Volatile);
        il.Emit(OpCodes.Stsfld, margins);
        il.Emit(OpCodes.Ret);
        return configure;
    }

    private static MethodDefinition AddTranslation(TypeDefinition mapping, TypeDefinition coordinates,
        TypeDefinition vector2, FieldDefinition margins, TypeDefinition screen)
    {
        var convert = new MethodDefinition("ToViewport",
            MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig, coordinates);
        var position = new ParameterDefinition("position", ParameterAttributes.None, coordinates);
        convert.Parameters.Add(position);
        mapping.Methods.Add(convert);
        var currentMargins = new VariableDefinition(vector2);
        convert.Body.Variables.Add(currentMargins);
        convert.Body.InitLocals = true;

        var il = convert.Body.GetILProcessor();
        var configured = Instruction.Create(OpCodes.Unbox_Any, vector2);
        var done = Instruction.Create(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Volatile);
        il.Emit(OpCodes.Ldsfld, margins);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Brtrue, configured);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Br, done);
        il.Append(configured);
        il.Emit(OpCodes.Stloc, currentMargins);

        // The CoreWindow reports a left-based X and Unity uses a bottom-left pixel origin.
        EmitAxis(il, position, coordinates.Fields.Single(field => field.Name == "x"), currentMargins,
            vector2.Fields.Single(field => field.Name == "x"),
            screen.Methods.Single(method => method.Name == "get_width"), OpCodes.Sub);
        EmitAxis(il, position, coordinates.Fields.Single(field => field.Name == "y"), currentMargins,
            vector2.Fields.Single(field => field.Name == "y"),
            screen.Methods.Single(method => method.Name == "get_height"), OpCodes.Add);
        il.Append(done);
        il.Emit(OpCodes.Ret);
        return convert;
    }

    private static void EmitAxis(ILProcessor il, ParameterDefinition position, FieldDefinition coordinate,
        VariableDefinition margins, FieldDefinition margin, MethodDefinition dimension, OpCode shift)
    {
        il.Emit(OpCodes.Ldarga, position);
        il.Emit(OpCodes.Ldarga, position);
        il.Emit(OpCodes.Ldfld, coordinate);
        il.Emit(OpCodes.Ldloca, margins);
        il.Emit(OpCodes.Ldfld, margin);
        il.Emit(OpCodes.Call, dimension);
        il.Emit(OpCodes.Conv_R4);
        il.Emit(OpCodes.Mul);
        il.Emit(shift);
        il.Emit(OpCodes.Stfld, coordinate);
    }

    private static void TranslateGetter(TypeDefinition input, string name, MethodDefinition convert)
    {
        var getter = input.Methods.Single(method => method.Name == name);
        var returnValue = getter.Body.Instructions.Single(op => op.OpCode == OpCodes.Ret);
        getter.Body.GetILProcessor().InsertBefore(returnValue, Instruction.Create(OpCodes.Call, convert));
    }
}
