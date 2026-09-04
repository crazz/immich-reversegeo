using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("ImmichReverseGeo.Web")]

namespace ImmichReverseGeo.Core.ApplicationRole;

/// <summary>
/// Identifies an application composition role.
/// </summary>
public sealed class ApplicationRole
{
    private ApplicationRole()
    {
    }

    public static ApplicationRole Web { get; } = new();

    public static ApplicationRole InternalWorker { get; } = new();

    public static ApplicationRole RunOnce { get; } = new();
}

/// <summary>
/// Identifies a role that can be supplied by public application composition.
/// </summary>
public sealed class PublicApplicationRole
{
    private PublicApplicationRole(ApplicationRole applicationRole)
    {
        ApplicationRole = applicationRole;
    }

    public static PublicApplicationRole Web { get; } = new(ApplicationRole.Web);

    public static PublicApplicationRole RunOnce { get; } = new(ApplicationRole.RunOnce);

    internal ApplicationRole ApplicationRole { get; }
}

/// <summary>
/// Represents the complete result of application-role selection.
/// </summary>
public abstract class ApplicationRoleSelectionResult
{
    private ApplicationRoleSelectionResult()
    {
    }

    internal static Success CreateSuccess(ApplicationRole role, IReadOnlyList<string> arguments)
    {
        return new Success(role, arguments);
    }

    internal static Failure CreateFailure(string category, string diagnostic)
    {
        return new Failure(category, diagnostic);
    }

    /// <summary>
    /// Represents a successful application-role selection.
    /// </summary>
    public sealed class Success : ApplicationRoleSelectionResult
    {
        internal Success(ApplicationRole role, IReadOnlyList<string> arguments)
        {
            Role = role;
            Arguments = arguments;
        }

        public ApplicationRole Role { get; }

        public IReadOnlyList<string> Arguments { get; }
    }

    /// <summary>
    /// Represents a safe failure to select an application role.
    /// </summary>
    public sealed class Failure : ApplicationRoleSelectionResult
    {
        internal Failure(string category, string diagnostic)
        {
            Category = category;
            Diagnostic = diagnostic;
        }

        public string Category { get; }

        public string Diagnostic { get; }
    }
}

/// <summary>
/// Selects the application composition role without reading process state.
/// </summary>
public static class ApplicationRoleSelector
{
    internal const string InternalWorkerSelector = "--internal-worker";
    private const string DuplicateCategory = "duplicate-internal-worker-selector";
    private const string UnexpectedArgumentCategory = "unexpected-internal-worker-argument";
    private const string InvalidSyntaxCategory = "invalid-internal-worker-syntax";

    public static ApplicationRoleSelectionResult Select(IReadOnlyList<string> arguments)
    {
        return Select(arguments, PublicApplicationRole.Web);
    }

    public static ApplicationRoleSelectionResult Select(
        IReadOnlyList<string> arguments,
        PublicApplicationRole publicRoleCandidate)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(publicRoleCandidate);

        var exactSelectorCount = 0;
        var hasMalformedReservedForm = false;

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];

            if (string.Equals(argument, InternalWorkerSelector, StringComparison.Ordinal))
            {
                exactSelectorCount++;
                continue;
            }

            if (string.Equals(argument, InternalWorkerSelector, StringComparison.OrdinalIgnoreCase)
                || argument.StartsWith($"{InternalWorkerSelector}=", StringComparison.OrdinalIgnoreCase))
            {
                hasMalformedReservedForm = true;
            }
        }

        if (exactSelectorCount >= 2)
        {
            return Failure(DuplicateCategory);
        }

        if (hasMalformedReservedForm)
        {
            return Failure(InvalidSyntaxCategory);
        }

        if (exactSelectorCount == 1)
        {
            if (arguments.Count == 1)
            {
                return Success(ApplicationRole.InternalWorker, []);
            }

            return Failure(UnexpectedArgumentCategory);
        }

        return Success(publicRoleCandidate.ApplicationRole, arguments);
    }

    private static ApplicationRoleSelectionResult.Success Success(ApplicationRole role, IReadOnlyList<string> arguments)
    {
        var copy = new string[arguments.Count];

        for (var index = 0; index < arguments.Count; index++)
        {
            copy[index] = arguments[index];
        }

        return ApplicationRoleSelectionResult.CreateSuccess(role, new ReadOnlyCollection<string>(copy));
    }

    private static ApplicationRoleSelectionResult.Failure Failure(string category)
    {
        return ApplicationRoleSelectionResult.CreateFailure(
            category,
            $"Application role selection failed: {category}. Supported private syntax: {InternalWorkerSelector}.");
    }
}
