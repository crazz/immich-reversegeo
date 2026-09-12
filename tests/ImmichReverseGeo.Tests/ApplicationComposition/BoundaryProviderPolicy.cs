using System.Reflection;
using System.Text.RegularExpressions;
using ImmichReverseGeo.Web.Composition;
using ImmichReverseGeo.Web.Services;
using Microsoft.Data.Sqlite;

namespace ImmichReverseGeo.Tests.ApplicationComposition;

internal static class BoundaryProviderPolicy
{
    private static readonly string[] MetadataQueries =
    [
        "SELECT substr(type, 1, 17), substr(name, 1, 129) FROM sqlite_schema LIMIT $limit;",
        "SELECT cid, substr(name, 1, 33), upper(substr(type, 1, 17)), \"notnull\", pk FROM pragma_table_info('_meta') LIMIT 3;",
        "SELECT typeof(value), substr(CAST(value AS TEXT), 1, $take) FROM _meta WHERE key = $key LIMIT 1;"
    ];

    internal static BoundaryDiagnostic? MetadataSql(string sql, string owner)
    {
        string normalized = Regex.Replace(sql.Trim(), @"\s+", " ", RegexOptions.CultureInvariant);
        return MetadataQueries.Contains(normalized, StringComparer.Ordinal) ? null
            : ControlPlaneDependencyPolicy.Diagnostic("ProviderScope", BoundaryRole.Standard, owner,
                "metadata SQLite", normalized, owner + " -> unreviewed query/mutation/export");
    }

    internal static bool ExplicitlyDisablesPooling(IReadOnlyList<object> body)
    {
        bool found = false;
        for (int index = 0; index < body.Count; index++)
        {
            if (body[index] is MethodBase method && method.DeclaringType == typeof(SqliteConnectionStringBuilder)
                && method.Name == "set_Pooling")
            {
                if (index == 0 || body[index - 1] is not 0)
                {
                    return false;
                }
                found = true;
            }
        }
        return found;
    }

    internal static IEnumerable<MethodBase> Methods(Type type)
    {
        const BindingFlags flags = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (MethodBase method in type.GetMethods(flags).Cast<MethodBase>().Concat(type.GetConstructors(flags)))
        {
            if (!method.IsAbstract && method.GetMethodBody() is not null)
            {
                yield return method;
            }
        }
        foreach (Type nested in type.GetNestedTypes(flags))
        {
            foreach (MethodBase method in Methods(nested))
            {
                yield return method;
            }
        }
    }

    internal static Type Owner(Type type)
    {
        while (type.DeclaringType is Type parent)
        {
            type = parent;
        }
        return type;
    }
}

[TestClass]
[TestCategory("Change56")]
public sealed class ControlPlaneProviderPolicyTests
{
    [TestMethod]
    public void CompiledSqliteOwnersAndInventoryQueries_ArePreciselyBounded()
    {
        var owners = new HashSet<Type>();
        foreach (Type type in typeof(StandardWebApplication).Assembly.GetTypes().Where(t => t.DeclaringType is null))
        {
            foreach (MethodBase method in BoundaryProviderPolicy.Methods(type))
            {
                if (BoundaryIlMetadata.Read(method).OfType<MethodBase>().Any(m => m is ConstructorInfo && m.DeclaringType == typeof(SqliteConnection)))
                {
                    owners.Add(type);
                }
            }
        }
        CollectionAssert.AreEquivalent(new[] { typeof(CacheInventorySqliteMetadataReader), typeof(SkippedAssetsRepository) }, owners.ToArray(),
            "A new SQLite implementation requires an exact provider-scope review, even in the allowed Web assembly.");
        string[] queries = BoundaryProviderPolicy.Methods(typeof(CacheInventorySqliteMetadataReader))
            .SelectMany(BoundaryIlMetadata.Read).OfType<string>()
            .Where(sql => Regex.IsMatch(sql.TrimStart(), @"^(SELECT|PRAGMA|WITH|INSERT|UPDATE|DELETE|CREATE|DROP|ATTACH|VACUUM)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .Distinct(StringComparer.Ordinal).ToArray();
        Assert.HasCount(3, queries, "All three compiled bounded schema/metadata queries are inspected, including async generated bodies.");
        foreach (string query in queries)
        {
            Assert.IsNull(BoundaryProviderPolicy.MetadataSql(query, nameof(CacheInventorySqliteMetadataReader)));
        }
        object[] readerMetadata = BoundaryProviderPolicy.Methods(typeof(CacheInventorySqliteMetadataReader))
            .SelectMany(BoundaryIlMetadata.Read).ToArray();
        Assert.AreEqual(1, readerMetadata.OfType<MethodBase>().Count(m => m is ConstructorInfo && m.DeclaringType == typeof(SqliteConnection)),
            "A second connection path needs its own metadata-scope review.");
        Assert.AreEqual(1, readerMetadata.OfType<MethodBase>().Count(m => m.DeclaringType == typeof(SqliteConnectionStringBuilder) && m.Name == "set_Pooling"),
            "The inventory reader has one explicit pooling decision.");
        Assert.IsTrue(BoundaryProviderPolicy.ExplicitlyDisablesPooling(readerMetadata),
            "The actual compiled inventory reader must explicitly disable SQLite pooling.");
    }

    [TestMethod]
    public void MetadataPoolingMustBeExplicitlyDisabledAndCannotBeSilentlyInferred()
    {
        MethodInfo setter = typeof(SqliteConnectionStringBuilder).GetProperty(nameof(SqliteConnectionStringBuilder.Pooling))!.SetMethod!;
        Assert.IsTrue(BoundaryProviderPolicy.ExplicitlyDisablesPooling([0, setter]));
        Assert.IsFalse(BoundaryProviderPolicy.ExplicitlyDisablesPooling([1, setter]));
        Assert.IsFalse(BoundaryProviderPolicy.ExplicitlyDisablesPooling([setter]));
        Assert.IsFalse(BoundaryProviderPolicy.ExplicitlyDisablesPooling([]));
        Assert.IsFalse(BoundaryProviderPolicy.ExplicitlyDisablesPooling([0, setter, 1, setter]), "A later true setter overrides the earlier false setter.");
        Assert.IsFalse(BoundaryProviderPolicy.ExplicitlyDisablesPooling([1, setter, 0, setter]), "Every path must disable pooling; a later false setter cannot justify an earlier true path.");
    }

    [TestMethod]
    [DataRow("SELECT COUNT(*) FROM division_area;")]
    [DataRow("SELECT geometry FROM gadm_area;")]
    [DataRow("DELETE FROM division_area;")]
    [DataRow("VACUUM INTO 'export.db';")]
    [DataRow("ATTACH DATABASE 'geodata.db' AS source;")]
    public void MetadataAllowance_RejectsGeodataCountsQueriesMutationAndExport(string sql)
    {
        BoundaryDiagnostic failure = BoundaryProviderPolicy.MetadataSql(sql, "approved metadata wrapper")!;
        Assert.IsNotNull(failure);
        Assert.AreEqual("ProviderScope", failure.Rule);
        Assert.AreEqual("metadata SQLite", failure.Category);
        Assert.AreEqual(sql, failure.Offender);
        Assert.AreEqual("approved metadata wrapper", failure.Root);
    }
}
