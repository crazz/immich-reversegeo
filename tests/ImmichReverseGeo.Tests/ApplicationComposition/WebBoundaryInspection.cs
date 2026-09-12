using System.Reflection;
using System.Runtime.CompilerServices;
using ImmichReverseGeo.Web.Composition;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace ImmichReverseGeo.Tests.ApplicationComposition;

// Canonical metadata walk for all four roles. Factory classification never resolves a provider.
internal sealed class WebBoundaryInspection(BoundaryRole role = BoundaryRole.Standard)
{
    private readonly HashSet<Type> _types = [];
    private readonly HashSet<MethodBase> _methods = [];
    private readonly List<BoundaryDiagnostic> _diagnostics = [];
    private IReadOnlyDictionary<Type, ServiceDescriptor[]> _descriptors = new Dictionary<Type, ServiceDescriptor[]>();
    private string _root = "";

    internal int InspectedFactories { get; private set; }
    internal IReadOnlyList<BoundaryDiagnostic> Diagnostics => ControlPlaneDependencyPolicy.Sort(_diagnostics);
    internal IReadOnlyList<string> Failures => Diagnostics.Select(d => d.ToString()).ToArray();

    internal static bool IsForbiddenAssembly(string? name)
    {
        return ControlPlaneDependencyPolicy.HeavyAssembly(name) is not null;
    }

    internal void Inspect(IEnumerable<ServiceDescriptor> descriptors, IEnumerable<Type> componentTypes)
    {
        ServiceDescriptor[] registrations = descriptors.OrderBy(d => d.ServiceType.FullName, StringComparer.Ordinal)
            .ThenBy(d => (d.IsKeyedService ? d.KeyedImplementationType : d.ImplementationType)?.FullName, StringComparer.Ordinal)
            .ThenBy(d => (d.IsKeyedService ? (Delegate?)d.KeyedImplementationFactory : d.ImplementationFactory)?.Method.Name, StringComparer.Ordinal)
            .ThenBy(d => d.ServiceKey as string, StringComparer.Ordinal).ToArray();
        _descriptors = registrations.GroupBy(d => d.ServiceType).ToDictionary(g => g.Key, g => g.ToArray());
        foreach (ServiceDescriptor descriptor in registrations.OrderBy(d => d.ServiceType.FullName, StringComparer.Ordinal))
        {
            string path = "descriptor " + descriptor.ServiceType;
            BeginRoot(path);
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
        foreach (Type component in componentTypes.OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            BeginRoot("component " + component.FullName);
            InspectType(component, "component " + component.FullName);
        }
    }

    private void BeginRoot(string root)
    {
        _root = root;
        _types.Clear();
        _methods.Clear();
    }

    private void Fail(string rule, string category, string offender, string path)
    {
        _diagnostics.Add(ControlPlaneDependencyPolicy.Diagnostic(rule, role, _root, category, offender, path));
    }

    internal static IEnumerable<PropertyInfo> Injections(Type type)
    {
        for (Type? current = type; current is not null; current = current.BaseType)
        {
            foreach (PropertyInfo property in current.GetProperties(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                if (property.IsDefined(typeof(InjectAttribute), inherit: true))
                {
                    yield return property;
                }
            }
        }
    }

    private void InspectType(Type type, string path)
    {
        if (ControlPlaneDependencyPolicy.ForbiddenType(type, role) is string category)
        {
            Fail("ForbiddenDependency", category, type.ToString(), path + " -> forbidden assembly/type");
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
        if (_descriptors.TryGetValue(type, out ServiceDescriptor[]? registrations))
        {
            foreach (ServiceDescriptor descriptor in registrations)
            {
                Type? implementation = descriptor.IsKeyedService ? descriptor.KeyedImplementationType : descriptor.ImplementationType;
                object? instance = descriptor.IsKeyedService ? descriptor.KeyedImplementationInstance : descriptor.ImplementationInstance;
                Delegate? factory = descriptor.IsKeyedService ? descriptor.KeyedImplementationFactory : descriptor.ImplementationFactory;
                if (implementation is not null)
                {
                    InspectType(implementation, path + " -> alias/implementation " + type.Name);
                }
                if (instance is not null)
                {
                    InspectType(instance.GetType(), path + " -> instance " + type.Name);
                }
                if (factory is not null)
                {
                    InspectFactory(factory, path + " -> factory " + type.Name);
                }
            }
        }
        if (!ControlPlaneDependencyPolicy.IsApplication(type))
        {
            return;
        }
        foreach (PropertyInfo property in Injections(type))
        {
            InspectType(property.PropertyType, path + " -> inject " + property.DeclaringType!.Name + "." + property.Name);
        }
        if (type.BaseType is Type parent && parent != typeof(object))
        {
            InspectType(parent, path + " -> base " + type.Name);
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
        if (factory.Method.DeclaringType is null)
        {
            Fail("UnclassifiedFactory", "factory metadata", factory.Method.Name, path + " -> missing declaring owner");
            return;
        }
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
                else if (value is not null)
                {
                    InspectType(value.GetType(), path + " -> captured instance " + field.Name);
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
        if (!ControlPlaneDependencyPolicy.IsApplication(method.DeclaringType))
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
            Fail("UnclassifiedFactory", "factory metadata", method.ToString()!, path + " -> opaque factory method");
            return;
        }
        foreach (object member in BoundaryIlMetadata.Read(method))
        {
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
                    if (ControlPlaneDependencyPolicy.IsWeb(role)
                        && called.DeclaringType?.Assembly.GetName().Name == "Npgsql"
                        && (called.Name.StartsWith("OpenConnection", StringComparison.Ordinal) || called.Name is "Open" or "OpenAsync"))
                    {
                        Fail("ProviderScope", "eager PostgreSQL", called.ToString()!, next + " -> factory connection");
                    }
                    if (called.DeclaringType == typeof(Activator) || called.DeclaringType == typeof(System.Runtime.InteropServices.NativeLibrary)
                        || (called.DeclaringType == typeof(Assembly) && called.Name.StartsWith("Load", StringComparison.Ordinal)))
                    {
                        Fail("UnclassifiedFactory", "opaque activation", called.ToString()!, next + " -> opaque activation " + called.Name);
                    }
                    InspectMethod(called, next);
                    break;
            }
        }
    }
}
