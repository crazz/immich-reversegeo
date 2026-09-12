using ImmichReverseGeo.Core.Countries;
using ImmichReverseGeo.Web.Services;

namespace ImmichReverseGeo.Tests.ApplicationComposition;

[TestClass]
[TestCategory("Change55")]
public sealed class CountryIdentityBoundaryTests
{
    [TestMethod]
    public void BundledIdentity_PreservesEveryMappingDisplayOrderAndSourceAlias()
    {
        var service = CountryCodeService.CreateForTest();
        var catalog = CountryIdentityCatalog.Load(Path.Combine(AppContext.BaseDirectory, "data", "iso3166.json"));
        Assert.HasCount(250, catalog.Identities);
        foreach (var identity in catalog.Identities)
        {
            Assert.AreEqual(identity.Alpha2, service.Iso3ToAlpha2(identity.Alpha3.ToLowerInvariant()));
            Assert.AreEqual(identity.Alpha3, service.Alpha2ToIso3(identity.Alpha2.ToLowerInvariant()));
            Assert.AreEqual(identity, service.FindByAlpha3(identity.Alpha3));
        }

        CollectionAssert.AreEqual(
            catalog.Identities.OrderBy(identity => identity.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(identity => identity.Alpha3).ToArray(),
            service.GetKnownCountries().Select(country => country.Iso3).ToArray());
        CollectionAssert.AreEqual(
            catalog.Identities.Select(identity => identity.Alpha3).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            service.GetKnownIso3Codes().ToArray());

        Assert.AreEqual("FRA", catalog.ResolveSourceAlpha2("cp")?.Alpha3);
        Assert.AreEqual("BES", catalog.ResolveSourceAlpha2("XE")?.Alpha3);
        Assert.AreEqual("SJM", catalog.ResolveSourceAlpha2("XJ")?.Alpha3);
        Assert.AreEqual("BES", catalog.ResolveSourceAlpha2("XS")?.Alpha3);
        Assert.AreEqual("XK", service.Iso3ToAlpha2("XKX"));
        Assert.IsNull(catalog.FindByAlpha2("CP"));
        Assert.IsNull(catalog.FindByAlpha3("ZZZ"));
        Assert.IsNull(catalog.ResolveSourceAlpha2(null));
        Assert.HasCount(24, catalog.ExplicitlyNonIsoAlpha2Codes);
        Assert.IsTrue(catalog.IsExplicitlyNonIso("xz"));
        Assert.AreEqual("XKO", GadmCountryCodeMapper.ToGadmCode(" xkx "));
        Assert.AreEqual("XKX", GadmCountryCodeMapper.ToAppCode("xko"));
    }
}
