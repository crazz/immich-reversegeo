using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;

namespace ImmichReverseGeo.Tests.ApplicationComposition;

// MethodBase belongs to an immutable loaded module. Cached values contain metadata only.
internal static class BoundaryIlMetadata
{
    private static readonly ConcurrentDictionary<MethodBase, IReadOnlyList<object>> Cache = new();
    private static readonly IReadOnlyDictionary<short, OpCode> Opcodes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.FieldType == typeof(OpCode))
        .Select(f => (OpCode)f.GetValue(null)!).ToDictionary(o => o.Value);

    internal static IReadOnlyList<object> Read(MethodBase method) => Cache.GetOrAdd(method, Parse);

    private static IReadOnlyList<object> Parse(MethodBase method)
    {
        byte[]? body = method.GetMethodBody()?.GetILAsByteArray();
        if (body is null)
        {
            return [];
        }
        var members = new List<object>();
        int offset = 0;
        while (offset < body.Length)
        {
            short value = body[offset++];
            if (value == 0xfe)
            {
                value = (short)(0xfe00 | body[offset++]);
            }
            OpCode opcode = Opcodes[value];
            int size = opcode.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(body, offset),
                _ => 4
            };
            if (opcode == OpCodes.Ldc_I4_0 || opcode == OpCodes.Ldc_I4_1)
            {
                members.Add(opcode == OpCodes.Ldc_I4_0 ? 0 : 1);
            }
            if (opcode.OperandType is OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineType or OperandType.InlineTok)
            {
                MemberInfo? member = method.Module.ResolveMember(BitConverter.ToInt32(body, offset),
                    method.DeclaringType?.GetGenericArguments(), method.IsGenericMethod ? method.GetGenericArguments() : null);
                if (member is not null)
                {
                    members.Add(member);
                }
            }
            else if (opcode.OperandType == OperandType.InlineString)
            {
                members.Add(method.Module.ResolveString(BitConverter.ToInt32(body, offset)));
            }
            offset += size;
        }
        return members.ToArray();
    }

    internal static IReadOnlyList<Type> FactoryDependencies(Delegate factory)
    {
        var found = new HashSet<Type>();
        var visited = new HashSet<MethodBase>();
        void Visit(MethodBase method)
        {
            if (!visited.Add(method))
            {
                return;
            }
            if (method.IsGenericMethod)
            {
                foreach (Type argument in method.GetGenericArguments())
                {
                    found.Add(argument);
                }
            }
            if (method is ConstructorInfo && method.DeclaringType is Type constructed)
            {
                found.Add(constructed);
                return;
            }
            if (!ControlPlaneDependencyPolicy.IsApplication(method.DeclaringType) || method.IsAbstract)
            {
                return;
            }
            foreach (object member in Read(method))
            {
                if (member is MethodBase called)
                {
                    Visit(called);
                }
                else if (member is Type type)
                {
                    found.Add(type);
                }
            }
        }
        Visit(factory.Method);
        return found.OrderBy(t => t.FullName, StringComparer.Ordinal).ToArray();
    }
}
