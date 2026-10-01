using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;

if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("Usage: ReportTransportCheck <current-plugin.dll> [old-plugin-positive-control.dll]");
    return 2;
}

try
{
    var candidate = Inspect(args[0]);
    bool passed = PrintResult(args[0], candidate, expectUnsafe: false);
    if (args.Length == 2)
        passed &= PrintResult(args[1], Inspect(args[1]), expectUnsafe: true);
    if (passed)
        Console.WriteLine("PASS: compiled plugin has no reporting or kick transport references.");
    return passed ? 0 : 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ERROR: {ex.GetType().Name}: {ex.Message}");
    return 2;
}

static bool PrintResult(string path, List<string> findings, bool expectUnsafe)
{
    bool passed = expectUnsafe ? findings.Count > 0 : findings.Count == 0;
    string label = expectUnsafe ? "old-version positive control" : "current plugin";
    Console.WriteLine($"{(passed ? "PASS" : "FAIL")}: {label}: {Path.GetFileName(path)} ({findings.Count} findings)");
    if (findings.Count > 0)
    {
        foreach (string finding in findings.Take(10))
            Console.WriteLine($"  {finding}");
        if (findings.Count > 10)
            Console.WriteLine($"  ... {findings.Count - 10} additional findings");
    }
    if (expectUnsafe && findings.Count == 0)
        Console.Error.WriteLine("The positive-control DLL must contain the old reporting transport.");
    return passed;
}

static List<string> Inspect(string path)
{
    using var stream = File.OpenRead(path);
    using var pe = new PEReader(stream);
    if (!pe.HasMetadata)
        throw new BadImageFormatException($"{path} is not a managed assembly.");
    MetadataReader reader = pe.GetMetadataReader();
    var findings = new List<string>();

    foreach (MemberReferenceHandle handle in reader.MemberReferences)
    {
        MemberReference member = reader.GetMemberReference(handle);
        string name = reader.GetString(member.Name);
        if (IsForbiddenName(name))
            findings.Add($"MemberReference: {DescribeType(reader, member.Parent)}.{name}");
    }

    foreach (TypeReferenceHandle handle in reader.TypeReferences)
    {
        TypeReference type = reader.GetTypeReference(handle);
        string name = reader.GetString(type.Name);
        if (IsForbiddenName(name))
            findings.Add($"TypeReference: {reader.GetString(type.Namespace)}.{name}");
    }

    Dictionary<ushort, OpCode> opcodes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode))
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(opcode => unchecked((ushort)opcode.Value));

    foreach (MethodDefinitionHandle handle in reader.MethodDefinitions)
    {
        MethodDefinition method = reader.GetMethodDefinition(handle);
        string methodName = reader.GetString(method.Name);
        if (IsForbiddenName(methodName))
            findings.Add($"MethodDefinition: {methodName}");
        if (method.RelativeVirtualAddress == 0)
            continue;
        byte[] il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()
            ?? throw new BadImageFormatException($"Missing IL for {methodName}.");
        for (int offset = 0; offset < il.Length;)
        {
            int instructionOffset = offset;
            ushort value = il[offset++];
            if (value == 0xfe)
            {
                RequireBytes(il, offset, 1, methodName);
                value = (ushort)(0xfe00 | il[offset++]);
            }
            if (!opcodes.TryGetValue(value, out OpCode opcode))
                throw new BadImageFormatException($"Unknown opcode in {methodName} at {instructionOffset}.");
            int size = OperandSize(opcode.OperandType, il, offset, methodName);
            RequireBytes(il, offset, size, methodName);
            if (opcode.OperandType == OperandType.InlineString)
            {
                int token = BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(offset, 4));
                if ((token & unchecked((int)0xff000000)) != 0x70000000)
                    throw new BadImageFormatException($"Invalid user-string token in {methodName}.");
                string text = reader.GetUserString(MetadataTokens.UserStringHandle(token & 0x00ffffff));
                if (IsForbiddenName(text))
                    findings.Add($"IL string in {methodName}: {text}");
            }
            offset += size;
        }
    }

    // Scan both metadata heaps even when an unused patch-target string has no ldstr instruction.
    byte[] metadata = pe.GetMetadata().GetContent().ToArray();
    foreach (string identifier in ForbiddenIdentifiers())
    {
        if (metadata.AsSpan().IndexOf(Encoding.UTF8.GetBytes(identifier)) >= 0)
            findings.Add($"Metadata UTF-8 string: {identifier}");
        if (metadata.AsSpan().IndexOf(Encoding.Unicode.GetBytes(identifier)) >= 0)
            findings.Add($"Metadata UTF-16 string: {identifier}");
    }
    return findings;
}

static string[] ForbiddenIdentifiers() =>
[
    "ReportPlayer", "ReportReasons", "ReportDatas", "OTSS_QUEUE_V1",
    "BanRPC", "KickRPC", "KickPlayer", "PlayerKick", "VoteKick", "KickVote"
];

static bool IsForbiddenName(string name)
{
    if (ForbiddenIdentifiers().Any(identifier => name.Contains(identifier, StringComparison.OrdinalIgnoreCase)))
        return true;
    return name.Contains("RPC", StringComparison.OrdinalIgnoreCase)
        && (name.Contains("report", StringComparison.OrdinalIgnoreCase)
            || name.Contains("kick", StringComparison.OrdinalIgnoreCase)
            || name.Contains("ban", StringComparison.OrdinalIgnoreCase));
}

static string DescribeType(MetadataReader reader, EntityHandle handle) => handle.Kind switch
{
    HandleKind.TypeReference => reader.GetString(reader.GetTypeReference((TypeReferenceHandle)handle).Name),
    HandleKind.TypeDefinition => reader.GetString(reader.GetTypeDefinition((TypeDefinitionHandle)handle).Name),
    _ => handle.Kind.ToString()
};

static int OperandSize(OperandType type, byte[] il, int offset, string methodName)
{
    switch (type)
    {
        case OperandType.InlineNone:
            return 0;
        case OperandType.ShortInlineBrTarget:
        case OperandType.ShortInlineI:
        case OperandType.ShortInlineVar:
            return 1;
        case OperandType.InlineVar:
            return 2;
        case OperandType.InlineBrTarget:
        case OperandType.InlineField:
        case OperandType.InlineI:
        case OperandType.InlineMethod:
        case OperandType.InlineSig:
        case OperandType.InlineString:
        case OperandType.InlineTok:
        case OperandType.InlineType:
        case OperandType.ShortInlineR:
            return 4;
        case OperandType.InlineI8:
        case OperandType.InlineR:
            return 8;
        case OperandType.InlineSwitch:
            RequireBytes(il, offset, 4, methodName);
            int count = BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(offset, 4));
            if (count < 0)
                throw new BadImageFormatException($"Negative switch count in {methodName}.");
            return checked(4 + count * 4);
        default:
            throw new BadImageFormatException($"Unsupported operand type {type} in {methodName}.");
    }
}

static void RequireBytes(byte[] il, int offset, int count, string methodName)
{
    if (count < 0 || offset < 0 || offset > il.Length - count)
        throw new BadImageFormatException($"Truncated IL in {methodName} at {offset}.");
}
