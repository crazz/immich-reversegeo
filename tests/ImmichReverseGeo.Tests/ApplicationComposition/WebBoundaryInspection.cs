using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using ImmichReverseGeo.Web.Composition;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace ImmichReverseGeo.Tests.ApplicationComposition;

// Change55 proof over the production registrations. Reads metadata; never invokes a DI factory.
internal sealed class WebBoundaryInspection
{
    private static readonly Assembly WebAssembly = typeof(StandardWebApplication).Assembly;
    private static readonly IReadOnlyDictionary<short, OpCode> Opcodes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode))
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(opcode => opcode.Value);
    private readonly HashSet<Type> _types = [];
    private readonly HashSet<MethodBase> _methods = [];
    private readonly List<string> _failures = [];

    internal int InspectedFactories { get; private set; }
    internal IReadOnlyList<string> Failures => _failures;

    internal static bool IsForbiddenAssembly(string? name)
    {
        return name is "ImmichReverseGeo.Overture" or "ImmichReverseGeo.Gadm" or "ImmichReverseGeo.Worker"
            || name?.StartsWith("DuckDB", StringComparison.Ordinal) == true
            || name?.StartsWith("NetTopologySuite", StringComparison.Ordinal) == true
            || name?.StartsWith("GeoJSON", StringComparison.Ordinal) == true;
    }

    internal void Inspect(IEnumerable<ServiceDescriptor> descriptors, IEnumerable<Type> componentTypes)
    {
        foreach (ServiceDescriptor descriptor in descriptors)
        {
            string path = "descriptor " + descriptor.ServiceType;
            InspectType(descriptor.ServiceType, path);
            Type? implementation = descriptor.IsKeyedService
                ? descriptor.KeyedImplementationType : descriptor.ImplementationType;
            object? instance = descriptor.IsKeyedService
                ? descriptor.KeyedImplementationInstance : descriptor.ImplementationInstance;
            Delegate? factory = descriptor.IsKeyedService
                ? descriptor.KeyedImplementationFactory : descriptor.ImplementationFactory;
            if (implementation is not null)
            {
                InspectType(implementation, path + " -> implementation");
            }
            if (instance is not null)
            {
                InspectType(instance.GetType(), path + " -> instance");
            }
            if (factory is not null)
            {
                InspectedFactories++;
                InspectFactory(factory, path + " -> factory");
            }
        }
        foreach (Type component in componentTypes)
        {
            InspectType(component, "component " + component.FullName);
            foreach (PropertyInfo property in component.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (property.IsDefined(typeof(InjectAttribute), inherit: true))
                {
                    InspectType(property.PropertyType, "component " + component.FullName + " -> inject " + property.Name);
                }
            }
        }
    }

    private void InspectType(Type type, string path)
    {
        if (IsForbiddenAssembly(type.Assembly.GetName().Name))
        {
            _failures.Add(path + " -> forbidden assembly/type " + type);
            return;
        }
        if (!_types.Add(type))
        {
            return;
        }
        foreach (Type argument in type.GetGenericArguments())
        {
            InspectType(argument, path + " -> generic " + type.Name);
        }
        if (type.HasElementType)
        {
            InspectType(type.GetElementType()!, path + " -> element");
        }
        if (type.Assembly != WebAssembly && type.Assembly != typeof(WebBoundaryInspection).Assembly)
        {
            return;
        }
        foreach (ConstructorInfo constructor in type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            foreach (ParameterInfo parameter in constructor.GetParameters())
            {
                InspectType(parameter.ParameterType, path + " -> constructor " + type.Name + "(" + parameter.Name + ")");
            }
        }
    }

    private void InspectFactory(Delegate factory, string path)
    {
        InspectMethod(factory.Method, path);
        if (factory.Target is not null && factory.Target.GetType().IsDefined(typeof(CompilerGeneratedAttribute)))
        {
            foreach (FieldInfo field in factory.Target.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                InspectType(field.FieldType, path + " -> capture " + field.Name);
                // Field access cannot execute a property getter or initialize a Lazy<T>.
                object? value = field.GetValue(factory.Target);
                if (value is Delegate callback)
                {
                    InspectFactory(callback, path + " -> captured callback " + field.Name);
                }
                else if (value is Type capturedType)
                {
                    InspectType(capturedType, path + " -> captured type " + field.Name);
                }
            }
        }
    }

    private void InspectMethod(MethodBase method, string path)
    {
        if (!_methods.Add(method))
        {
            return;
        }
        if (method.DeclaringType is Type owner)
        {
            InspectType(owner, path + " -> " + method.Name);
        }
        if (method is MethodInfo information)
        {
            InspectType(information.ReturnType, path + " -> return");
            foreach (Type argument in information.GetGenericArguments())
            {
                InspectType(argument, path + " -> " + method.Name);
            }
        }
        if (method.DeclaringType?.Assembly != WebAssembly
            && method.DeclaringType?.Assembly != typeof(WebBoundaryInspection).Assembly)
        {
            return; // Framework factory metadata is bounded by the separate package/reference guard.
        }
        if (method.IsAbstract)
        {
            return; // Interface implementations are inspected through the complete descriptor set.
        }
        byte[]? body = method.GetMethodBody()?.GetILAsByteArray();
        if (body is null)
        {
            _failures.Add(path + " -> opaque factory method " + method);
            return;
        }
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
            if (opcode.OperandType is OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineType or OperandType.InlineTok)
            {
                MemberInfo? member = method.Module.ResolveMember(BitConverter.ToInt32(body, offset),
                    method.DeclaringType?.GetGenericArguments(), method.IsGenericMethod ? method.GetGenericArguments() : null);
                string next = path + " -> " + method.Name;
                switch (member)
                {
                    case Type type:
                        InspectType(type, next);
                        break;
                    case FieldInfo field:
                        InspectType(field.FieldType, next + " -> field " + field.Name);
                        break;
                    case MethodBase called:
                        if (called.DeclaringType == typeof(Activator)
                            || (called.DeclaringType == typeof(Assembly) && called.Name.StartsWith("Load", StringComparison.Ordinal)))
                        {
                            _failures.Add(next + " -> opaque activation " + called.Name);
                        }
                        InspectMethod(called, next);
                        break;
                }
            }
            offset += size;
        }
    }
}
