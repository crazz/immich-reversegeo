using System.Reflection;
using System.Runtime.CompilerServices;
using ImmichReverseGeo.Tests.ApplicationComposition;
using ImmichReverseGeo.Web.Services;
using Npgsql;

namespace ImmichReverseGeo.Tests;

[TestClass]
[TestCategory("Change58")]
public sealed class ScheduledExistenceRepositoryTests
{
    [TestMethod]
    public void ScalarDecoderAcceptsOnlyBooleansWithoutConvertingFailureToNoWork()
    {
        Assert.IsTrue(ImmichDbRepository.DecodeExistenceResult(true));
        Assert.IsFalse(ImmichDbRepository.DecodeExistenceResult(false));
        foreach (object? value in new object?[] { null, DBNull.Value, 0L, 1L, 0, "false", new object() })
        {
            var failure = Assert.ThrowsExactly<InvalidOperationException>(() => ImmichDbRepository.DecodeExistenceResult(value));
            Assert.AreEqual("The eligibility query did not return a boolean.", failure.Message);
        }
    }

    [TestMethod]
    public void CompiledRepositoryBoundaryHasOneScalarReadAndNoCountParametersOrTimeoutOverride()
    {
        MethodInfo method = typeof(ImmichDbRepository).GetMethod(nameof(ImmichDbRepository.HasUnprocessedAssetsAsync))!;
        Assert.AreEqual(typeof(Task<bool>), method.ReturnType);
        CollectionAssert.AreEqual(new[] { typeof(CancellationToken) }, method.GetParameters().Select(p => p.ParameterType).ToArray());
        Type machine = method.GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType;
        MethodBase[] calls = BoundaryIlMetadata.Read(machine.GetMethod("MoveNext", BindingFlags.NonPublic | BindingFlags.Instance)!)
            .OfType<MethodBase>().ToArray();
        Assert.AreEqual(1, calls.Count(m => m.Name == nameof(NpgsqlDataSource.OpenConnectionAsync)));
        Assert.AreEqual(1, calls.Count(m => m.Name == nameof(NpgsqlCommand.ExecuteScalarAsync)));
        Assert.IsFalse(calls.Any(m => m.Name is "get_Parameters" or "set_CommandTimeout" or "ExecuteNonQueryAsync"
            or "ExecuteReaderAsync" or nameof(ImmichDbRepository.GetUnprocessedCountAsync)));
        string sql = ScheduledExistencePostgresFixture.ExistenceSql;
        Assert.IsTrue(sql.TrimStart().StartsWith("SELECT EXISTS", StringComparison.Ordinal));
        Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(sql, @"\bCOUNT\s*\(",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        Assert.IsFalse(sql.Contains('@'));
    }
}
