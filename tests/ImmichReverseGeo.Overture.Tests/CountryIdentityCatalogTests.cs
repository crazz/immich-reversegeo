using ImmichReverseGeo.Overture.Services;

namespace ImmichReverseGeo.Overture.Tests;

[TestClass]
public class CountryIdentityCatalogTests
{
    [TestMethod]
    public void LegacyFlatMapping_RemainsUsable()
    {
        WithCatalog("""{"CHE":"CH","HKG":"HK","XKX":"XK"}""", catalog =>
        {
            Assert.AreEqual(3, catalog.Identities.Count);
            Assert.AreEqual("CHE", catalog.FindByAlpha2("ch")?.Alpha3);
            Assert.AreEqual("HK", catalog.FindByAlpha3("hkg")?.Alpha2);
            Assert.AreEqual("XKX", catalog.FindByAlpha2("XK")?.Alpha3);
        });
    }

    [TestMethod]
    public void SourceAliases_ResolveWithoutBecomingCanonicalIdentities()
    {
        WithCatalog("""
            {"iso3ToAlpha2":{"BES":"BQ","FRA":"FR","SJM":"SJ"},
             "displayNamesByAlpha2":{"BQ":"Bonaire, Sint Eustatius and Saba","FR":"France","SJ":"Svalbard and Jan Mayen"},
             "sourceAlpha2Aliases":{"XE":"BQ","XS":"BQ","CP":"FR","XJ":"SJ"},
             "explicitlyNonIsoAlpha2Codes":["XX"]}
            """, catalog =>
        {
            Assert.AreEqual("BES", catalog.ResolveSourceAlpha2("xs")?.Alpha3);
            Assert.AreEqual("BES", catalog.ResolveSourceAlpha2("XE")?.Alpha3);
            Assert.AreEqual("FRA", catalog.ResolveSourceAlpha2("CP")?.Alpha3);
            Assert.AreEqual("SJM", catalog.ResolveSourceAlpha2("XJ")?.Alpha3);
            Assert.IsNull(catalog.FindByAlpha2("XS"));
            Assert.IsNull(catalog.ResolveSourceAlpha2("XX"));
            Assert.IsTrue(catalog.IsExplicitlyNonIso("xx"));
            Assert.IsNull(catalog.FindByAlpha3("unknown"));
            Assert.AreEqual(3, catalog.Identities.Count);
        });
    }

    [TestMethod]
    public void AliasTargetMustExist()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => WithCatalog("""
            {"iso3ToAlpha2":{"CHE":"CH"},"sourceAlpha2Aliases":{"XS":"BQ"}}
            """, _ => { }));
    }

    [TestMethod]
    public void DuplicateCanonicalAlpha2_IsRejected()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => WithCatalog("""
            {"iso3ToAlpha2":{"CHE":"CH","BAD":"CH"}}
            """, _ => { }));
    }

    private static void WithCatalog(string json, Action<CountryIdentityCatalog> check)
    {
        var path = Path.Combine(Path.GetTempPath(), $"reversegeo-catalog-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, json);
            check(CountryIdentityCatalog.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}