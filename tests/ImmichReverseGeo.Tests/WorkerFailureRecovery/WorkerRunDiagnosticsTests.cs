using System.Text;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.WorkerFailureRecovery;

namespace ImmichReverseGeo.Tests.WorkerFailureRecovery;

[TestClass]
[TestCategory("Change30")]
public class WorkerRunDiagnosticsTests
{
    [TestMethod]
    public void RenderExcerpt_PreservesPlainSafeLines()
    {
        // This optional export deliberately prefers redacting structured, delimiter-bearing text
        // over preserving log fidelity; only plain status lines are safe to retain.
        const string text = "Worker finished normally\nSummary written";

        var excerpt = WorkerRunDiagnostics.RenderExcerpt(text);

        Assert.AreEqual(text, excerpt);
    }

    [TestMethod]
    public void RenderExcerpt_StripsControlAndFormatCharactersBeforeMatching()
    {
        var safe = WorkerRunDiagnostics.RenderExcerpt("Worker\u0001 finished\u202E normally");
        var ansiHiddenSecret = WorkerRunDiagnostics.RenderExcerpt("pass\u001b[31mword secret");

        Assert.AreEqual("Worker finished normally", safe);
        AssertNoControlOrFormatCharacters(safe);
        AssertRedacted(ansiHiddenSecret, "password", "secret");
        AssertNoControlOrFormatCharacters(ansiHiddenSecret);
    }

    [TestMethod]
    public void RenderExcerpt_RedactsReplacementDecodedStandardErrorTail()
    {
        var bytes = Encoding.UTF8.GetBytes("password=").Concat(new byte[] { 0xff }).Concat(Encoding.UTF8.GetBytes("secret")).ToArray();
        var tail = new ChildWorkerStandardErrorTail(bytes, bytes.Length, false, false);

        var excerpt = WorkerRunDiagnostics.RenderExcerpt(tail.Text);

        Assert.IsTrue(tail.Text.Contains('\ufffd'), "invalid-utf8-decodes-with-replacement");
        AssertRedacted(excerpt, "password", "secret");
    }

    [TestMethod]
    public void RenderExcerpt_BoundsInputInspectionAndOutput()
    {
        var input = new string('a', 65_536) + " password=outside-inspection-bound";

        var excerpt = WorkerRunDiagnostics.RenderExcerpt(input);

        Assert.IsTrue(excerpt.Length <= WorkerRunDiagnostics.ExcerptLimit, "output-is-bounded");
        Assert.IsTrue(excerpt.EndsWith("[truncated]", StringComparison.Ordinal), "input-bound-is-marked-truncated");
        Assert.IsFalse(excerpt.Contains("outside-inspection-bound", StringComparison.Ordinal), "uninspected-tail-is-not-exported");
    }

    [TestMethod]
    public void RenderExcerpt_RedactionRetainsTruncationMarker()
    {
        var excerpt = WorkerRunDiagnostics.RenderExcerpt("password=secret", tailWasTruncated: true);

        Assert.AreEqual("[redacted]\n[truncated]", excerpt);
    }

    [TestMethod]
    public void RenderExcerpt_RedactsEveryStructuredOrSensitiveMarkerAcrossTheWholeExcerpt()
    {
        var cases = new (string Name, string Text, string[] Secrets)[]
        {
            ("password", "password=super-secret", ["super-secret"]),
            ("uri-userinfo", "https://alice:uri-secret@example.test/path", ["alice", "uri-secret"]),
            ("connection-string", "Server=db;User Id=alice;Password=connection-secret", ["alice", "connection-secret"]),
            ("authorization-bearer", "Authorization: Bearer bearer-secret", ["bearer-secret"]),
            ("generic-token", "abcdefghijklmnopqrstuvwxyz0123456789", ["abcdefghijklmnopqrstuvwxyz0123456789"]),
            ("mixed-case-key", "PaSsWoRd: mixed-case-secret", ["mixed-case-secret"]),
            ("key-delimiter", "api_key{delimiter-secret}", ["delimiter-secret"]),
            ("json", "{\"password\":\"json-secret\"}", ["json-secret"]),
            ("execute", "execute --connection execute-secret", ["execute-secret"]),
            ("request-payload", "request payload=request-secret", ["request-secret"]),
            ("sql", "select value from assets where secret=sql-secret", ["sql-secret"]),
            ("multiline-continuation", "Worker finished normally\npassword=first-secret\ncontinuation second-secret", ["first-secret", "second-secret"])
        };

        foreach (var test in cases)
        {
            var excerpt = WorkerRunDiagnostics.RenderExcerpt(test.Text);

            AssertRedacted(excerpt, test.Secrets);
        }
    }

    [TestMethod]
    public void Describe_EveryFailureCategoryIsBoundedAndNeverIncludesUntrustedInput()
    {
        const string sentinel = "TOP_SECRET_VALUE";

        foreach (var category in Enum.GetValues<WorkerRunFailureCategory>())
        {
            var message = WorkerRunDiagnostics.Describe(category);

            Assert.IsFalse(string.IsNullOrWhiteSpace(message), $"diagnostic-present-{category}");
            Assert.IsTrue(message.Length <= 256, $"diagnostic-bounded-{category}");
            Assert.IsFalse(message.Contains(sentinel, StringComparison.Ordinal), $"diagnostic-has-no-input-{category}");
            AssertNoControlOrFormatCharacters(message);
        }
    }

    private static void AssertRedacted(string excerpt, params string[] secrets)
    {
        Assert.IsTrue(excerpt.StartsWith("[redacted]", StringComparison.Ordinal), "entire-export-is-redacted");
        foreach (var secret in secrets)
        {
            Assert.IsFalse(excerpt.Contains(secret, StringComparison.OrdinalIgnoreCase), $"secret-not-exported-{secret}");
        }
    }

    private static void AssertNoControlOrFormatCharacters(string value)
    {
        foreach (var character in value)
        {
            Assert.IsFalse(char.IsControl(character), $"control-character-{(int)character}");
            Assert.AreNotEqual(System.Globalization.UnicodeCategory.Format, char.GetUnicodeCategory(character), $"format-character-{(int)character}");
        }
    }
}
