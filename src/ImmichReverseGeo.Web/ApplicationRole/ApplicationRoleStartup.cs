using System;
using System.Collections.Generic;
using System.IO;
using ImmichReverseGeo.Core.ApplicationRole;
using Role = ImmichReverseGeo.Core.ApplicationRole.ApplicationRole;

namespace ImmichReverseGeo.Web.ApplicationRole;

public static class ApplicationRoleStartup
{
    public static void Begin(
        IReadOnlyList<string> arguments,
        TextWriter errorWriter,
        Action<IReadOnlyList<string>> webContinuation,
        Action<int> exitCodeSink)
    {
        Begin(arguments, PublicApplicationRole.Web, errorWriter, webContinuation, exitCodeSink);
    }

    public static void Begin(
        IReadOnlyList<string> arguments,
        PublicApplicationRole publicRoleCandidate,
        TextWriter errorWriter,
        Action<IReadOnlyList<string>> webContinuation,
        Action<int> exitCodeSink)
    {
        var selection = ApplicationRoleSelector.Select(arguments, publicRoleCandidate);

        if (selection is ApplicationRoleSelectionResult.Failure failure)
        {
            errorWriter.WriteLine(failure.Diagnostic);
            exitCodeSink(2);
            return;
        }

        var success = (ApplicationRoleSelectionResult.Success)selection;

        if (ReferenceEquals(success.Role, Role.Web))
        {
            webContinuation(success.Arguments);
            return;
        }

        if (ReferenceEquals(success.Role, Role.InternalWorker))
        {
            return;
        }

        if (ReferenceEquals(success.Role, Role.RunOnce))
        {
            return;
        }
    }
}
