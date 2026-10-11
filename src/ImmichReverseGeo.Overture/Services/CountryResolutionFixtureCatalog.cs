using System.Collections.Generic;
using ImmichReverseGeo.Overture.Models;

namespace ImmichReverseGeo.Overture.Services;

public static class CountryResolutionFixtureCatalog
{
    public static IReadOnlyList<CountryResolutionFixture> MandatoryTerritories { get; } =
    [
        new("Hong Kong Island", 22.2812, 114.1719, "Hong Kong", "HKG", "HK", "CN"),
        new("Hong Kong Island second coordinate", 22.2783, 114.1719, "Hong Kong", "HKG", "HK", "CN"),
        new("Macao", 22.1987, 113.5439, "Macao", "MAC", "MO", "CN"),
        new("Greenland", 64.1814, -51.6941, "Greenland", "GRL", "GL", "DK"),
        new("Faroe Islands", 62.0079, -6.7900, "Faroe Islands", "FRO", "FO", "DK"),
        new("Jersey", 49.1868, -2.1066, "Jersey", "JEY", "JE", "GB"),
        new("Guernsey", 49.4568, -2.5820, "Guernsey", "GGY", "GG", "GB"),
        new("Isle of Man", 54.1523, -4.4861, "Isle of Man", "IMN", "IM", "GB"),
        new("Puerto Rico", 18.4655, -66.1057, "Puerto Rico", "PRI", "PR", "US"),
        new("Guam", 13.4443, 144.7937, "Guam", "GUM", "GU", "US"),
        new("U.S. Virgin Islands", 18.3419, -64.9307, "U.S. Virgin Islands", "VIR", "VI", "US"),
        new("Bermuda", 32.2949, -64.7814, "Bermuda", "BMU", "BM", "GB"),
        new("Gibraltar", 36.1408, -5.3536, "Gibraltar", "GIB", "GI", "GB"),
        new("Cayman Islands", 19.2866, -81.3744, "Cayman Islands", "CYM", "KY", "GB"),
        new("British Virgin Islands", 18.4286, -64.6185, "British Virgin Islands", "VGB", "VG", "GB"),
        new("Aruba", 12.5211, -69.9683, "Aruba", "ABW", "AW", "NL"),
        new("Curaçao", 12.1696, -68.9900, "Curaçao", "CUW", "CW", "NL"),
        new("Åland Islands", 60.0973, 19.9348, "Åland Islands", "ALA", "AX", "FI"),
        new("Réunion", -20.8789, 55.4481, "Réunion", "REU", "RE", "FR"),
        new("French Polynesia", -17.5516, -149.5585, "French Polynesia", "PYF", "PF", "FR"),
        new("New Caledonia", -22.2758, 166.4580, "New Caledonia", "NCL", "NC", "FR")
    ];

    public static IReadOnlyList<CountryResolutionFixture> ParentCountryControls { get; } =
    [
        new("Mainland China", 39.9042, 116.4074, "China", "CHN", "CN"),
        new("Mainland Denmark", 55.6761, 12.5683, "Denmark", "DNK", "DK"),
        new("Mainland United Kingdom", 51.5074, -0.1278, "United Kingdom", "GBR", "GB"),
        new("Mainland United States", 38.9072, -77.0369, "United States", "USA", "US"),
        new("Netherlands", 52.3676, 4.9041, "Netherlands", "NLD", "NL"),
        new("Mainland Finland", 60.1699, 24.9384, "Finland", "FIN", "FI"),
        new("Metropolitan France", 48.8566, 2.3522, "France", "FRA", "FR")
    ];
}
