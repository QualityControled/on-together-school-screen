using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

static class CompiledTransportChecks
{
    public static void Run(string pluginPath, string gamePath)
    {
        using var pluginStream = File.OpenRead(pluginPath);
        using var pluginPe = new PEReader(pluginStream);
        MetadataReader plugin = pluginPe.GetMetadataReader();
        using var gameStream = File.OpenRead(gamePath);
        using var gamePe = new PEReader(gameStream);
        MetadataReader game = gamePe.GetMetadataReader();
        MethodDefinition prefix = Find(plugin, "SchoolScreenPlugin", "ReceiveBoardCommand");
        MethodDefinition original = Find(game, "QuadPainterGPU", "FillTheBlanksRPC_Original_2");
        var types = new TypeNames();
        var prefixTypes = prefix.DecodeSignature(types, null);
        var originalTypes = original.DecodeSignature(types, null);
        Dictionary<string, string> gameParams = NamedParameters(game, original, originalTypes.ParameterTypes);
        Dictionary<string, string> prefixParams = NamedParameters(plugin, prefix, prefixTypes.ParameterTypes);
        if (prefixTypes.ReturnType != "System.Boolean") throw new InvalidOperationException("Harmony prefix must return Boolean.");
        if (!prefixParams.TryGetValue("__instance", out string instanceType) || instanceType != "QuadPainterGPU")
            throw new InvalidOperationException("Harmony __instance does not match the actual game board type.");
        foreach (var parameter in prefixParams)
        {
            if (parameter.Key == "__instance") continue;
            if (!gameParams.TryGetValue(parameter.Key, out string actual) || actual != parameter.Value)
                throw new InvalidOperationException($"Harmony parameter {parameter.Key}:{parameter.Value} does not match the actual game signature.");
        }
        if (!prefixParams.TryGetValue("rpcInfo", out string rpcType) || rpcType != "PurrNet.RPCInfo")
            throw new InvalidOperationException("Harmony prefix must receive the actual rpcInfo sender context.");
        int boardCalls = 0;
        foreach (var methodHandle in plugin.MethodDefinitions)
        {
            MethodDefinition method = plugin.GetMethodDefinition(methodHandle);
            if (method.RelativeVirtualAddress == 0) continue;
            var instructions = ReadIl(pluginPe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes());
            for (int i = 0; i < instructions.Count; i++)
            {
                var instruction = instructions[i];
                if (instruction.Code.OperandType != OperandType.InlineMethod) continue;
                EntityHandle called = MetadataTokens.EntityHandle(instruction.Token);
                if (called.Kind != HandleKind.MemberReference) continue;
                MemberReference member = plugin.GetMemberReference((MemberReferenceHandle)called);
                string name = plugin.GetString(member.Name);
                if (member.Parent.Kind == HandleKind.TypeReference)
                {
                    string owner = plugin.GetString(plugin.GetTypeReference((TypeReferenceHandle)member.Parent).Name);
                    if (owner == "TextChannelManager" && !name.StartsWith("get_", StringComparison.Ordinal))
                        throw new InvalidOperationException("Unexpected game chat-manager method call: " + name);
                }
                if (name.Contains("RPC", StringComparison.OrdinalIgnoreCase) && name != "FillTheBlanksRPC")
                    throw new InvalidOperationException("Unexpected outgoing RPC reference: " + name);
                if (name.Contains("ReportPlayer", StringComparison.OrdinalIgnoreCase) || name.Contains("KickPlayer", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Unexpected report/kick call: " + name);
                if (name != "FillTheBlanksRPC") continue;
                boardCalls++;
                var signature = member.DecodeMethodSignature(types, null);
                int flagOffset = 2;
                if (signature.ParameterTypes.Length == 6 && signature.ParameterTypes[^1] == "PurrNet.RPCInfo")
                {
                    // C# expands the game RPC's optional sixth argument to default(RPCInfo).
                    // Account for that exact initobj/load sequence after the two bool flags.
                    if (i < 5 || instructions[i - 3].Code != OpCodes.Ldloca_S || instructions[i - 2].Code != OpCodes.Initobj
                        || !IsLocalLoad(instructions[i - 1].Code))
                        throw new InvalidOperationException("Compiled RPC default sender context is not the expected default(RPCInfo).");
                    EntityHandle infoType = MetadataTokens.EntityHandle(instructions[i - 2].Token);
                    if (infoType.Kind != HandleKind.TypeReference
                        || types.GetTypeFromReference(plugin, (TypeReferenceHandle)infoType, 0) != "PurrNet.RPCInfo")
                        throw new InvalidOperationException("Compiled RPC default context initializes an unexpected type.");
                    flagOffset = 5;
                }
                else if (signature.ParameterTypes.Length != 5)
                    throw new InvalidOperationException("Unexpected compiled board RPC signature.");
                if (plugin.GetString(method.Name) != "SendBoardPayload" || i < flagOffset
                    || instructions[i - flagOffset].Code != OpCodes.Ldc_I4_1 || instructions[i - flagOffset + 1].Code != OpCodes.Ldc_I4_0)
                    throw new InvalidOperationException("Compiled board RPC bypasses wrapper or lacks literal small-erase flags: " + string.Join(",", instructions.Skip(Math.Max(0, i - 8)).Take(Math.Min(i, 8)).Select(item => item.Code.Name)));
            }
        }
        if (boardCalls != 1) throw new InvalidOperationException($"Expected one compiled outgoing board RPC call site; found {boardCalls}.");
        Console.WriteLine("PASS: compiled DLL has one outgoing RPC call with small-erase flags and no other RPC calls; Harmony prefix names/types match the supplied actual game DLL.");
    }

    static bool IsLocalLoad(OpCode code) => code == OpCodes.Ldloc || code == OpCodes.Ldloc_S || code == OpCodes.Ldloc_0
        || code == OpCodes.Ldloc_1 || code == OpCodes.Ldloc_2 || code == OpCodes.Ldloc_3;

    static MethodDefinition Find(MetadataReader reader, string typeName, string methodName)
    {
        foreach (TypeDefinitionHandle handle in reader.TypeDefinitions)
        {
            TypeDefinition type = reader.GetTypeDefinition(handle);
            if (reader.GetString(type.Name) != typeName) continue;
            foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
            {
                MethodDefinition method = reader.GetMethodDefinition(methodHandle);
                if (reader.GetString(method.Name) == methodName) return method;
            }
        }
        throw new InvalidOperationException($"Missing required compiled method: {typeName}.{methodName}");
    }
    static Dictionary<string, string> NamedParameters(MetadataReader reader, MethodDefinition method, ImmutableArray<string> types)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (ParameterHandle handle in method.GetParameters())
        {
            Parameter parameter = reader.GetParameter(handle);
            if (parameter.SequenceNumber == 0) continue;
            result.Add(reader.GetString(parameter.Name), types[parameter.SequenceNumber - 1]);
        }
        return result;
    }
    static List<(OpCode Code, int Token)> ReadIl(byte[] il)
    {
        var known = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(OpCode)).Select(field => (OpCode)field.GetValue(null))
            .ToDictionary(code => unchecked((ushort)code.Value));
        var result = new List<(OpCode, int)>();
        for (int offset = 0; offset < il.Length;)
        {
            ushort value = il[offset++];
            if (value == 0xfe) value = (ushort)(0xfe00 | il[offset++]);
            OpCode code = known[value];
            int size = code.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => checked(4 + BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(offset, 4)) * 4),
                _ => 4
            };
            int token = code.OperandType is OperandType.InlineMethod or OperandType.InlineType ? BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(offset, 4)) : 0;
            result.Add((code, token)); offset += size;
        }
        return result;
    }

    sealed class TypeNames : ISignatureTypeProvider<string, object>
    {
        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[]";
        public string GetByReferenceType(string elementType) => elementType + "&";
        public string GetFunctionPointerType(MethodSignature<string> signature) => "function";
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType + "<" + string.Join(',', typeArguments) + ">";
        public string GetGenericMethodParameter(object context, int index) => "!!" + index;
        public string GetGenericTypeParameter(object context, int index) => "!" + index;
        public string GetModifiedType(string modifier, string unmodifiedType, bool required) => unmodifiedType;
        public string GetPinnedType(string elementType) => elementType;
        public string GetPointerType(string elementType) => elementType + "*";
        public string GetPrimitiveType(PrimitiveTypeCode code) => code switch
        {
            PrimitiveTypeCode.Boolean => "System.Boolean", PrimitiveTypeCode.Int32 => "System.Int32",
            PrimitiveTypeCode.Void => "System.Void", _ => "System." + code
        };
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        { var type = reader.GetTypeDefinition(handle); return Full(reader, type.Namespace, type.Name); }
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        { var type = reader.GetTypeReference(handle); return Full(reader, type.Namespace, type.Name); }
        public string GetTypeFromSpecification(MetadataReader reader, object context, TypeSpecificationHandle handle, byte rawTypeKind) => reader.GetTypeSpecification(handle).DecodeSignature(this, context);
        static string Full(MetadataReader reader, StringHandle ns, StringHandle name) => string.IsNullOrEmpty(reader.GetString(ns)) ? reader.GetString(name) : reader.GetString(ns) + "." + reader.GetString(name);
    }
}
