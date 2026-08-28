using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using ImmichReverseGeo.Overture.Models;

namespace ImmichReverseGeo.Overture.Services;

public sealed class CountryIdentityCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IReadOnlyDictionary<string, CountryIdentity> _byAlpha2;
    private readonly IReadOnlyDictionary<string, CountryIdentity> _byAlpha3;
    private readonly IReadOnlyCollection<CountryIdentity> _identities;
    private readonly IReadOnlyDictionary<string, string> _sourceAlpha2Aliases;
    private readonly IReadOnlySet<string> _explicitlyNonIsoAlpha2Codes;

    private CountryIdentityCatalog(
        IReadOnlyDictionary<string, CountryIdentity> byAlpha2,
        IReadOnlyDictionary<string, CountryIdentity> byAlpha3,
        IReadOnlyDictionary<string, string> sourceAlpha2Aliases,
        IReadOnlySet<string> explicitlyNonIsoAlpha2Codes)
    {
        _byAlpha2 = byAlpha2;
        _byAlpha3 = byAlpha3;
        _identities = byAlpha2.Values.ToArray();
        _sourceAlpha2Aliases = sourceAlpha2Aliases;
        _explicitlyNonIsoAlpha2Codes = explicitlyNonIsoAlpha2Codes;
    }

    public IReadOnlyCollection<CountryIdentity> Identities => _identities;

    public IReadOnlySet<string> ExplicitlyNonIsoAlpha2Codes => _explicitlyNonIsoAlpha2Codes;

    public static CountryIdentityCatalog Load(string path)
    {
        if (!File.Exists(path))
        {
            return Empty();
        }

        var document = JsonSerializer.Deserialize<CountryIdentityCatalogDocument>(File.ReadAllText(path), JsonOptions)
            ?? new CountryIdentityCatalogDocument();
        var displayNames = new Dictionary<string, string>(
            document.DisplayNamesByAlpha2 ?? new Dictionary<string, string>(),
            StringComparer.OrdinalIgnoreCase);
        var byAlpha2 = new Dictionary<string, CountryIdentity>(StringComparer.OrdinalIgnoreCase);
        var byAlpha3 = new Dictionary<string, CountryIdentity>(StringComparer.OrdinalIgnoreCase);

        foreach (var mapping in document.Iso3ToAlpha2 ?? new Dictionary<string, string>())
        {
            var alpha3 = mapping.Key.Trim().ToUpperInvariant();
            var alpha2 = mapping.Value.Trim().ToUpperInvariant();
            if (alpha3.Length == 0 || alpha2.Length == 0)
            {
                continue;
            }

            var displayName = displayNames.TryGetValue(alpha2, out var configuredName)
                ? configuredName
                : GetRegionDisplayName(alpha2);
            var identity = new CountryIdentity(displayName, alpha2, alpha3);

            if (!byAlpha2.TryAdd(alpha2, identity))
            {
                throw new InvalidDataException($"Duplicate Alpha-2 identity '{alpha2}' in {path}.");
            }

            if (!byAlpha3.TryAdd(alpha3, identity))
            {
                throw new InvalidDataException($"Duplicate Alpha-3 identity '{alpha3}' in {path}.");
            }
        }

        var sourceAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var alias in document.SourceAlpha2Aliases ?? new Dictionary<string, string>())
        {
            var sourceAlpha2 = alias.Key.Trim().ToUpperInvariant();
            var canonicalAlpha2 = alias.Value.Trim().ToUpperInvariant();
            if (!byAlpha2.ContainsKey(canonicalAlpha2))
            {
                throw new InvalidDataException(
                    $"Source Alpha-2 alias '{sourceAlpha2}' targets unknown canonical identity '{canonicalAlpha2}' in {path}.");
            }

            sourceAliases[sourceAlpha2] = canonicalAlpha2;
        }

        var explicitlyNonIso = (document.ExplicitlyNonIsoAlpha2Codes ?? [])
            .Select(code => code.Trim().ToUpperInvariant())
            .Where(code => code.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new CountryIdentityCatalog(byAlpha2, byAlpha3, sourceAliases, explicitlyNonIso);
    }

    public CountryIdentity? FindByAlpha2(string? alpha2)
    {
        return !string.IsNullOrWhiteSpace(alpha2) && _byAlpha2.TryGetValue(alpha2, out var identity)
            ? identity
            : null;
    }

    public CountryIdentity? ResolveSourceAlpha2(string? sourceAlpha2)
    {
        if (string.IsNullOrWhiteSpace(sourceAlpha2))
        {
            return null;
        }

        if (_sourceAlpha2Aliases.TryGetValue(sourceAlpha2, out var canonicalAlpha2))
        {
            return FindByAlpha2(canonicalAlpha2);
        }

        return FindByAlpha2(sourceAlpha2);
    }

    public CountryIdentity? FindByAlpha3(string? alpha3)
    {
        return !string.IsNullOrWhiteSpace(alpha3) && _byAlpha3.TryGetValue(alpha3, out var identity)
            ? identity
            : null;
    }

    public bool IsExplicitlyNonIso(string? alpha2)
    {
        return !string.IsNullOrWhiteSpace(alpha2) && _explicitlyNonIsoAlpha2Codes.Contains(alpha2);
    }

    private static CountryIdentityCatalog Empty()
    {
        return new CountryIdentityCatalog(
            new Dictionary<string, CountryIdentity>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, CountryIdentity>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    private static string GetRegionDisplayName(string alpha2)
    {
        try
        {
            return new RegionInfo(alpha2).EnglishName;
        }
        catch (ArgumentException)
        {
            return alpha2;
        }
    }

    private sealed class CountryIdentityCatalogDocument
    {
        public Dictionary<string, string>? Iso3ToAlpha2 { get; init; }

        public Dictionary<string, string>? DisplayNamesByAlpha2 { get; init; }

        public Dictionary<string, string>? SourceAlpha2Aliases { get; init; }

        public string[]? ExplicitlyNonIsoAlpha2Codes { get; init; }
    }
}
