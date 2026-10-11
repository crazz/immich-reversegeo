using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Overture.Models;
using ImmichReverseGeo.Overture.Services;
using Microsoft.Extensions.Logging;

namespace ImmichReverseGeo.Web.Services;

public class CountryCodeService
{
    private readonly CountryIdentityCatalog _catalog;

    public CountryCodeService(ILogger<CountryCodeService> logger, StorageOptions dirs)
    {
        var sw = Stopwatch.StartNew();
        logger.LogInformation("CountryCodeService: loading ISO 3166 identities");
        _catalog = LoadCatalog(dirs.BundledDataDir);
        logger.LogInformation(
            "CountryCodeService: ISO 3166 identities loaded ({Count} entries) in {Elapsed}ms",
            _catalog.Identities.Count,
            sw.ElapsedMilliseconds);
    }

    public CountryCodeService(string bundledDataDir)
    {
        _catalog = LoadCatalog(bundledDataDir);
    }

    public static CountryCodeService CreateForTest(string bundledDataDir = "data")
    {
        return new CountryCodeService(bundledDataDir);
    }

    public string? Iso3ToAlpha2(string iso3)
    {
        return _catalog.FindByAlpha3(iso3)?.Alpha2;
    }

    public string? Alpha2ToIso3(string alpha2)
    {
        return _catalog.FindByAlpha2(alpha2)?.Alpha3;
    }

    public CountryIdentity? FindByAlpha2(string alpha2)
    {
        return _catalog.FindByAlpha2(alpha2);
    }

    public CountryIdentity? FindByAlpha3(string alpha3)
    {
        return _catalog.FindByAlpha3(alpha3);
    }

    public IReadOnlyList<string> GetKnownIso3Codes()
    {
        return _catalog.Identities
            .Select(identity => identity.Alpha3)
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<KnownCountryOption> GetKnownCountries()
    {
        return _catalog.Identities
            .Select(identity => new KnownCountryOption(
                identity.Alpha3,
                identity.Alpha2,
                identity.DisplayName))
            .OrderBy(country => country.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static CountryIdentityCatalog LoadCatalog(string bundledDataDir)
    {
        return CountryIdentityCatalog.Load(Path.Combine(bundledDataDir, "iso3166.json"));
    }
}

public record KnownCountryOption(string Iso3, string Alpha2, string DisplayName)
{
    public string Label => $"{DisplayName} ({Iso3})";
}
