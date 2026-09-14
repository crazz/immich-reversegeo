using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace ImmichReverseGeo.Tests;

internal sealed record CompiledMethodContract(string MethodId, string DeclaringType, string[] ParameterTypes, bool Active);
internal sealed record CompiledCaseBinding(string CaseId, string MethodId, string BindingKind, object?[] OrderedArguments);

internal static class ExecutorVerificationCatalog
{
    internal static IReadOnlyList<string> ScenarioIds => ExecutorContractAuthority.Document.ScenarioIds;
    internal static IReadOnlyList<string> TaskIds => ExecutorContractAuthority.Document.TaskIds;
    internal static IReadOnlyList<string> ExternalGateIds => ExecutorContractAuthority.Document.ExternalGateIds;
    internal static IReadOnlyList<ExecutorProofBindingContract> ProofBindings => ExecutorContractAuthority.Document.ProofBindings;
    internal static IReadOnlyList<CompiledMethodContract> Methods { get; } = ExecutorContractAuthority.Document.Methods
        .Select(item => new CompiledMethodContract(item.MethodId, item.DeclaringType!, item.ParameterTypes.ToArray(), item.Active)).ToArray();
    internal static IReadOnlyDictionary<string, CompiledCaseBinding> Bindings { get; } = ExecutorContractAuthority.Document.Contracts
        .ToDictionary(item => item.CaseId, item => new CompiledCaseBinding(item.CaseId, item.Binding.MethodId, item.Binding.BindingKind,
            item.Binding.OrderedArguments.Select(argument => ConvertArgument(argument)).ToArray()), StringComparer.Ordinal);

    internal static IReadOnlyList<Type> SupportTypes { get; } =
    [
        typeof(FixedUtcTimeProvider), typeof(AsyncGate), typeof(TestLogEntry), typeof(CaptureLogger),
        typeof(RecordingFaultReporter), typeof(TestSinkException), typeof(ExecutorFixture), typeof(ExecutorAssertions),
        typeof(ExecutorCallContract), typeof(ExecutorCallObservation), typeof(ExecutorEffectContract),
        typeof(ExecutorEventContract), typeof(ExecutorEventObservation), typeof(ExecutorDispositionObservation),
        typeof(ExecutorCaseContract), typeof(ExecutorCaseObservation), typeof(ExecutorContractAuthority),
        typeof(ExecutorCaseContractEngine), typeof(ExecutorVerificationCatalog), typeof(TransitiveIlWalker)
    ];

    internal static void AssertCompletePartitionsForSchemaTest(
        IEnumerable<ExecutorCaseContract> contracts,
        IEnumerable<ExecutorProofBindingContract> proofs)
    {
        var exactContracts = contracts.ToArray();
        var exactProofs = proofs.ToArray();
        foreach (var proof in exactProofs)
        {
            Assert.IsTrue(proof.ScenarioIds.All(ScenarioIds.Contains), proof.ProofId);
            Assert.IsTrue(proof.TaskIds.All(TaskIds.Contains), proof.ProofId);
            Assert.IsTrue(proof.SemanticClauses.Length > 0, proof.ProofId);
        }
        foreach (var scenarioId in ScenarioIds)
        {
            Assert.IsTrue(exactContracts.Any(contract => contract.ScenarioIds.Contains(scenarioId))
                || exactProofs.Any(proof => proof.ScenarioIds.Contains(scenarioId)), $"Unbound scenario {scenarioId}.");
        }
        foreach (var taskId in TaskIds)
        {
            Assert.IsTrue(exactContracts.Any(contract => contract.TaskIds.Contains(taskId))
                || exactProofs.Any(proof => proof.TaskIds.Contains(taskId)), $"Unbound task {taskId}.");
        }
    }

    internal static Type ResolveType(string name) => name switch
    {
        "System.String" => typeof(string),
        "System.Int32" => typeof(int),
        _ => throw new AssertFailedException($"Unsupported compiled parameter type {name}.")
    };

    private static object? ConvertArgument(ContractArgument argument) => argument.Type switch
    {
        "System.String" => argument.Value.GetString(),
        "System.Int32" => argument.Value.GetInt32(),
        _ => throw new AssertFailedException($"Unsupported contract argument type {argument.Type}.")
    };
}

internal sealed record IlWalkResult(IReadOnlySet<MethodBase> Methods, IReadOnlySet<MemberInfo> Members);

internal static class TransitiveIlWalker
{
    private static readonly IReadOnlyDictionary<short, OpCode> Codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode)).Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(code => code.Value);

    internal static IlWalkResult Walk(IEnumerable<MethodBase> roots, Assembly testAssembly)
    {
        var pending = new Queue<MethodBase>(roots.SelectMany(Implementations));
        var methods = new HashSet<MethodBase>();
        var members = new HashSet<MemberInfo>();
        while (pending.TryDequeue(out var method))
        {
            if (!methods.Add(method))
            {
                continue;
            }
            foreach (var member in ReferencedMembers(method))
            {
                members.Add(member);
                if (member is MethodBase next && next.Module.Assembly == testAssembly)
                {
                    foreach (var implementation in Implementations(next))
                    {
                        pending.Enqueue(implementation);
                    }
                }
            }
        }
        return new IlWalkResult(methods, members);
    }

    private static IEnumerable<MethodBase> Implementations(MethodBase method)
    {
        yield return method;
        if (method is MethodInfo info)
        {
            var stateMachine = info.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType;
            var moveNext = stateMachine?.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (moveNext is not null)
            {
                yield return moveNext;
            }
        }
    }

    internal static bool IsForbiddenFilesystemMember(MemberInfo member)
    {
        if (member is MethodInfo manifest
            && manifest.DeclaringType == typeof(Assembly)
            && manifest.Name == nameof(Assembly.GetManifestResourceStream))
        {
            return false;
        }
        if (member is Type referencedType && IsForbiddenFilesystemType(referencedType))
        {
            return true;
        }
        if (member is FieldInfo field && IsForbiddenFilesystemType(field.FieldType))
        {
            return true;
        }
        if (member is PropertyInfo property && IsForbiddenFilesystemType(property.PropertyType))
        {
            return true;
        }
        if (member is MethodBase method && method.GetParameters().Any(parameter => IsForbiddenFilesystemType(parameter.ParameterType)))
        {
            return true;
        }
        if (member is MethodInfo info && IsForbiddenFilesystemType(info.ReturnType))
        {
            return true;
        }
        var type = member.DeclaringType;
        if (type?.Namespace != "System.IO")
        {
            return false;
        }
        if (type == typeof(Stream))
        {
            return member.Name is not (nameof(Stream.Dispose) or nameof(Stream.Close) or nameof(Stream.Read)
                or nameof(Stream.ReadAsync) or "get_CanRead" or "get_Position" or "set_Position");
        }
        if (type == typeof(MemoryStream))
        {
            return member.Name is not (".ctor" or nameof(MemoryStream.ToArray) or nameof(MemoryStream.Write)
                or nameof(MemoryStream.WriteAsync) or nameof(MemoryStream.Read) or nameof(MemoryStream.ReadAsync)
                or "get_Position" or "set_Position");
        }
        if (type == typeof(StreamReader))
        {
            return member.Name is not (".ctor" or nameof(StreamReader.ReadToEnd) or nameof(StreamReader.ReadToEndAsync)
                or nameof(StreamReader.ReadLine) or nameof(StreamReader.ReadLineAsync) or nameof(StreamReader.Dispose)
                or nameof(StreamReader.Close));
        }
        if (type == typeof(TextReader))
        {
            return member.Name is not (nameof(TextReader.ReadToEnd) or nameof(TextReader.ReadToEndAsync)
                or nameof(TextReader.ReadLine) or nameof(TextReader.ReadLineAsync) or nameof(TextReader.Dispose)
                or nameof(TextReader.Close));
        }
        return true;
    }

    private static bool IsForbiddenFilesystemType(Type type)
    {
        if (type.IsByRef || type.IsArray || type.IsPointer)
        {
            return IsForbiddenFilesystemType(type.GetElementType()!);
        }
        if (type.IsGenericType && type.GetGenericArguments().Any(IsForbiddenFilesystemType))
        {
            return true;
        }
        return type.Namespace == "System.IO" && type != typeof(Stream) && type != typeof(MemoryStream) && type != typeof(StreamReader);
    }

    internal static bool ContainsSynchronousAwaiterGetResult(MethodBase method)
    {
        var calls = ReferencedMembers(method).OfType<MethodBase>().ToArray();
        for (var index = 0; index + 1 < calls.Length; index++)
        {
            if (calls[index].Name == "GetAwaiter" && calls[index + 1].Name == "GetResult")
            {
                return true;
            }
        }
        return false;
    }

    private static IEnumerable<MemberInfo> ReferencedMembers(MethodBase method)
    {
        var body = method.GetMethodBody();
        if (body is null)
        {
            yield break;
        }
        var bytes = body.GetILAsByteArray()!;
        for (var index = 0; index < bytes.Length;)
        {
            var first = bytes[index++];
            var value = first == 0xFE ? (short)(0xFE00 | bytes[index++]) : first;
            if (!Codes.TryGetValue(value, out var code))
            {
                throw new AssertFailedException($"Unknown IL opcode 0x{value:X4} in {method}.");
            }
            var operandSize = OperandSize(code.OperandType, bytes, index);
            if (code.OperandType is OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineType or OperandType.InlineTok)
            {
                var token = BitConverter.ToInt32(bytes, index);
                MemberInfo? member = null;
                try
                {
                    member = method.Module.ResolveMember(token, method.DeclaringType?.GetGenericArguments(), method is MethodInfo generic ? generic.GetGenericArguments() : null);
                }
                catch (ArgumentException)
                {
                }
                if (member is not null)
                {
                    yield return member;
                }
            }
            index += operandSize;
        }
    }

    private static int OperandSize(OperandType type, byte[] bytes, int index) => type switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI or OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineMethod
            or OperandType.InlineSig or OperandType.InlineString or OperandType.InlineTok or OperandType.InlineType
            or OperandType.ShortInlineR => 4,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => 4 + (BitConverter.ToInt32(bytes, index) * 4),
        _ => throw new AssertFailedException($"Unsupported IL operand {type}.")
    };
}

internal static class ExcludedRootIsolationIlSentinel
{
    internal static async Task ValidRootAsync()
    {
        await ValidHelperAsync().ConfigureAwait(false);
        RequiredMarker();
    }

    internal static async Task UnboundNeighborAsync()
    {
        await Task.Delay(1).ConfigureAwait(false);
        UnboundMarker();
        _ = File.ReadAllText("unbound-neighbor");
    }

    private static Task ValidHelperAsync() => Task.CompletedTask;
    internal static void RequiredMarker()
    {
    }
    internal static void UnboundMarker()
    {
    }
}

internal static class ExcludedForbiddenIlSentinel
{
    internal static async Task ExecuteAsync()
    {
        Func<Task<string>> hidden = async () =>
        {
            await Task.Delay(1).ConfigureAwait(false);
            var info = new FileInfo("sentinel");
            using var fileStream = new FileStream(info.FullName, FileMode.OpenOrCreate, FileAccess.ReadWrite);
            _ = Directory.EnumerateFiles(info.DirectoryName ?? ".").ToArray();
            return File.ReadAllText(info.FullName);
        };
        _ = hidden().GetAwaiter().GetResult();
    }
}
