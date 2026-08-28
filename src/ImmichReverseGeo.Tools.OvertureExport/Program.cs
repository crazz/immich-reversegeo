using System;
using System.Globalization;
using System.IO;
using DuckDB.NET.Data;
using ImmichReverseGeo.Overture.Services;
using Microsoft.Data.Sqlite;

string? temporaryOutputPath = null;
try
{
    var options = ExportOptions.Parse(args);
    var outputPath = Path.GetFullPath(options.OutputPath);
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

    var release = options.Release ?? GetLatestRelease();
    var tmpPath = outputPath + ".tmp";
    temporaryOutputPath = tmpPath;

    if (File.Exists(tmpPath))
    {
        File.Delete(tmpPath);
    }

    Console.WriteLine($"Overture release: {release}");
    Console.WriteLine($"Output path: {outputPath}");
    Console.WriteLine(options.Dataset == ExportDataset.CountryDivisions
        ? "Exporting country division areas..."
        : "Exporting airport infrastructure...");

    var rowCount = options.Dataset == ExportDataset.CountryDivisions
        ? ExportCountryDivisions(
            release,
            string.Format(CultureInfo.InvariantCulture, OvertureDivisionsLogic.DivisionAreaReleaseUrlTemplate, release),
            tmpPath,
            CountryIdentityCatalog.Load(Path.Combine(AppContext.BaseDirectory, "bundled-data", "iso3166.json")))
        : ExportAirportInfrastructure(
            release,
            string.Format(CultureInfo.InvariantCulture, OverturePlacesLogic.InfrastructureReleaseUrlTemplate, release),
            tmpPath);

    File.Move(tmpPath, outputPath, overwrite: true);
    temporaryOutputPath = null;

    var fileInfo = new FileInfo(outputPath);
    Console.WriteLine($"Rows exported: {rowCount:N0}");
    Console.WriteLine($"File size: {fileInfo.Length:N0} bytes ({fileInfo.Length / 1024d / 1024d:0.00} MB)");
    Console.WriteLine("Done.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.ToString());
    return 1;
}
finally
{
    if (temporaryOutputPath is not null && File.Exists(temporaryOutputPath))
    {
        try
        {
            File.Delete(temporaryOutputPath);
        }
        catch (IOException)
        {
            // Preserve the original export result; a stale temporary file is deleted on the next run.
        }
    }
}

static long ExportAirportInfrastructure(string release, string releaseUrl, string sqlitePath)
{
    using var duck = new DuckDBConnection("Data Source=:memory:");
    duck.Open();
    OvertureDataAccess.LoadHttpfs(duck);
    OvertureDataAccess.LoadAzureAndSpatial(duck);

    using var selectCmd = duck.CreateCommand();
    selectCmd.CommandText = $"""
        SELECT
            id,
            COALESCE(names.common['en'], names.primary, id) AS name,
            type AS feature_type,
            subtype,
            class,
            ST_Y(ST_CENTROID(geometry)) AS latitude,
            ST_X(ST_CENTROID(geometry)) AS longitude,
            ST_AsWKB(geometry) AS geom_wkb,
            bbox.xmin AS bbox_xmin,
            bbox.ymin AS bbox_ymin,
            bbox.xmax AS bbox_xmax,
            bbox.ymax AS bbox_ymax
        FROM read_parquet('{releaseUrl}', filename = true, hive_partitioning = 1)
        WHERE subtype = 'airport'
          AND (
                COALESCE(class, '') = 'airport'
                OR COALESCE(class, '') LIKE '%_airport'
              )
          AND COALESCE(class, '') NOT IN ('airport_gate', 'taxiway', 'runway', 'apron')
        """;
    using var reader = selectCmd.ExecuteReader();

    using var sqlite = new SqliteConnection($"Data Source={sqlitePath};Pooling=false");
    sqlite.Open();

    using (var ddl = sqlite.CreateCommand())
    {
        ddl.CommandText = """
            CREATE TABLE infrastructure (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                feature_type TEXT NULL,
                subtype TEXT NULL,
                class_name TEXT NULL,
                latitude REAL NOT NULL,
                longitude REAL NOT NULL,
                geom_wkb BLOB NULL,
                bbox_xmin REAL NULL,
                bbox_ymin REAL NULL,
                bbox_xmax REAL NULL,
                bbox_ymax REAL NULL
            );
            CREATE TABLE _meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE INDEX idx_infrastructure_bbox_x ON infrastructure (bbox_xmin, bbox_xmax);
            CREATE INDEX idx_infrastructure_bbox_y ON infrastructure (bbox_ymin, bbox_ymax);
            CREATE INDEX idx_infrastructure_subtype ON infrastructure (subtype);
            CREATE INDEX idx_infrastructure_class_name ON infrastructure (class_name);
            INSERT INTO _meta VALUES ('release', $release);
            INSERT INTO _meta VALUES ('downloadedAt', $downloadedAt);
            """;
        ddl.Parameters.AddWithValue("$release", release);
        ddl.Parameters.AddWithValue("$downloadedAt", DateTime.UtcNow.ToString("O"));
        ddl.ExecuteNonQuery();
    }

    using var tx = sqlite.BeginTransaction();
    using var insert = sqlite.CreateCommand();
    insert.CommandText = """
        INSERT INTO infrastructure (
            id, name, feature_type, subtype, class_name, latitude, longitude,
            geom_wkb, bbox_xmin, bbox_ymin, bbox_xmax, bbox_ymax
        ) VALUES (
            $id, $name, $featureType, $subtype, $className, $lat, $lon,
            $geom, $xmin, $ymin, $xmax, $ymax
        )
        """;

    var pId = insert.Parameters.Add("$id", SqliteType.Text);
    var pName = insert.Parameters.Add("$name", SqliteType.Text);
    var pFeatureType = insert.Parameters.Add("$featureType", SqliteType.Text);
    var pSubtype = insert.Parameters.Add("$subtype", SqliteType.Text);
    var pClassName = insert.Parameters.Add("$className", SqliteType.Text);
    var pLat = insert.Parameters.Add("$lat", SqliteType.Real);
    var pLon = insert.Parameters.Add("$lon", SqliteType.Real);
    var pGeom = insert.Parameters.Add("$geom", SqliteType.Blob);
    var pXMin = insert.Parameters.Add("$xmin", SqliteType.Real);
    var pYMin = insert.Parameters.Add("$ymin", SqliteType.Real);
    var pXMax = insert.Parameters.Add("$xmax", SqliteType.Real);
    var pYMax = insert.Parameters.Add("$ymax", SqliteType.Real);

    long rowCount = 0;
    while (reader.Read())
    {
        pId.Value = reader.GetString(0);
        pName.Value = reader.GetString(1);
        pFeatureType.Value = reader.IsDBNull(2) ? DBNull.Value : reader.GetString(2);
        pSubtype.Value = reader.IsDBNull(3) ? DBNull.Value : reader.GetString(3);
        pClassName.Value = reader.IsDBNull(4) ? DBNull.Value : reader.GetString(4);
        pLat.Value = reader.GetDouble(5);
        pLon.Value = reader.GetDouble(6);
        pGeom.Value = reader.IsDBNull(7) ? DBNull.Value : ReadBlobValue(reader.GetValue(7));
        pXMin.Value = reader.IsDBNull(8) ? DBNull.Value : reader.GetDouble(8);
        pYMin.Value = reader.IsDBNull(9) ? DBNull.Value : reader.GetDouble(9);
        pXMax.Value = reader.IsDBNull(10) ? DBNull.Value : reader.GetDouble(10);
        pYMax.Value = reader.IsDBNull(11) ? DBNull.Value : reader.GetDouble(11);
        insert.ExecuteNonQuery();
        rowCount++;
    }

    tx.Commit();

    using var compact = sqlite.CreateCommand();
    compact.CommandText = "VACUUM;";
    compact.ExecuteNonQuery();
    
    return rowCount;
}

static long ExportCountryDivisions(
    string release,
    string releaseUrl,
    string sqlitePath,
    CountryIdentityCatalog identityCatalog)
{
    using var duck = new DuckDBConnection("Data Source=:memory:");
    duck.Open();
    OvertureDataAccess.LoadHttpfs(duck);
    OvertureDataAccess.LoadAzureAndSpatial(duck);

    using var selectCmd = duck.CreateCommand();
    selectCmd.CommandText = $"""
        SELECT
            id,
            COALESCE(names.common['en'], names.primary, id) AS name,
            subtype,
            class,
            admin_level,
            country,
            COALESCE(is_land, true) AS is_land,
            COALESCE(is_territorial, false) AS is_territorial,
            ST_AsWKB(geometry) AS geom_wkb,
            bbox.xmin AS bbox_xmin,
            bbox.ymin AS bbox_ymin,
            bbox.xmax AS bbox_xmax,
            bbox.ymax AS bbox_ymax
        FROM read_parquet('{releaseUrl}', filename = true, hive_partitioning = 1)
        WHERE subtype IN ('country', 'dependency')
        """;
    using var reader = selectCmd.ExecuteReader();

    using var sqlite = new SqliteConnection($"Data Source={sqlitePath};Pooling=false");
    sqlite.Open();

    using (var ddl = sqlite.CreateCommand())
    {
        ddl.CommandText = """
            CREATE TABLE division_area (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                source_name TEXT NOT NULL,
                subtype TEXT NOT NULL,
                class_name TEXT NULL,
                admin_level INTEGER NULL,
                country TEXT NOT NULL,
                source_country TEXT NOT NULL,
                alpha3 TEXT NULL,
                is_land INTEGER NOT NULL,
                is_territorial INTEGER NOT NULL,
                geom_wkb BLOB NOT NULL,
                bbox_xmin REAL NOT NULL,
                bbox_ymin REAL NOT NULL,
                bbox_xmax REAL NOT NULL,
                bbox_ymax REAL NOT NULL
            );
            CREATE TABLE _meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE INDEX idx_division_area_bbox_x ON division_area (bbox_xmin, bbox_xmax);
            CREATE INDEX idx_division_area_bbox_y ON division_area (bbox_ymin, bbox_ymax);
            CREATE INDEX idx_division_area_subtype ON division_area (subtype);
            CREATE INDEX idx_division_area_admin_level ON division_area (admin_level);
            CREATE INDEX idx_division_area_country ON division_area (country);
            CREATE INDEX idx_division_area_alpha3 ON division_area (alpha3);
            INSERT INTO _meta VALUES ('release', $release);
            INSERT INTO _meta VALUES ('downloadedAt', $downloadedAt);
            INSERT INTO _meta VALUES ('sourceSubtypes', 'country,dependency');
            """;
        ddl.Parameters.AddWithValue("$release", release);
        ddl.Parameters.AddWithValue("$downloadedAt", DateTime.UtcNow.ToString("O"));
        ddl.ExecuteNonQuery();
    }

    using var tx = sqlite.BeginTransaction();
    using var insert = sqlite.CreateCommand();
    insert.CommandText = """
        INSERT INTO division_area (
            id, name, source_name, subtype, class_name, admin_level, country, source_country, alpha3,
            is_land, is_territorial, geom_wkb, bbox_xmin, bbox_ymin, bbox_xmax, bbox_ymax
        ) VALUES (
            $id, $name, $sourceName, $subtype, $className, $adminLevel, $country, $sourceCountry, $alpha3,
            $isLand, $isTerritorial, $geom, $xmin, $ymin, $xmax, $ymax
        )
        """;

    var pId = insert.Parameters.Add("$id", SqliteType.Text);
    var pName = insert.Parameters.Add("$name", SqliteType.Text);
    var pSourceName = insert.Parameters.Add("$sourceName", SqliteType.Text);
    var pSubtype = insert.Parameters.Add("$subtype", SqliteType.Text);
    var pClassName = insert.Parameters.Add("$className", SqliteType.Text);
    var pAdminLevel = insert.Parameters.Add("$adminLevel", SqliteType.Integer);
    var pCountry = insert.Parameters.Add("$country", SqliteType.Text);
    var pSourceCountry = insert.Parameters.Add("$sourceCountry", SqliteType.Text);
    var pAlpha3 = insert.Parameters.Add("$alpha3", SqliteType.Text);
    var pIsLand = insert.Parameters.Add("$isLand", SqliteType.Integer);
    var pIsTerritorial = insert.Parameters.Add("$isTerritorial", SqliteType.Integer);
    var pGeom = insert.Parameters.Add("$geom", SqliteType.Blob);
    var pXMin = insert.Parameters.Add("$xmin", SqliteType.Real);
    var pYMin = insert.Parameters.Add("$ymin", SqliteType.Real);
    var pXMax = insert.Parameters.Add("$xmax", SqliteType.Real);
    var pYMax = insert.Parameters.Add("$ymax", SqliteType.Real);

    long rowCount = 0;
    while (reader.Read())
    {
        var id = reader.GetString(0);
        var sourceName = reader.GetString(1);
        var sourceAlpha2 = reader.IsDBNull(5) ? null : reader.GetString(5).ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(sourceAlpha2))
        {
            throw new InvalidDataException($"Overture country row '{id}' has no Alpha-2 identity.");
        }

        var identity = identityCatalog.ResolveSourceAlpha2(sourceAlpha2);
        if (identity is null && !identityCatalog.IsExplicitlyNonIso(sourceAlpha2))
        {
            throw new InvalidDataException($"Overture country row '{id}' uses unmapped standard identity '{sourceAlpha2}'.");
        }

        pId.Value = id;
        pName.Value = identity?.DisplayName ?? sourceName;
        pSourceName.Value = sourceName;
        pSubtype.Value = reader.GetString(2);
        pClassName.Value = reader.IsDBNull(3) ? DBNull.Value : reader.GetString(3);
        pAdminLevel.Value = reader.IsDBNull(4) ? DBNull.Value : reader.GetInt32(4);
        pCountry.Value = identity?.Alpha2 ?? sourceAlpha2;
        pSourceCountry.Value = sourceAlpha2;
        pAlpha3.Value = identity?.Alpha3 ?? (object)DBNull.Value;
        pIsLand.Value = reader.GetBoolean(6) ? 1 : 0;
        pIsTerritorial.Value = reader.GetBoolean(7) ? 1 : 0;
        pGeom.Value = reader.IsDBNull(8)
            ? throw new InvalidDataException($"Overture country row '{id}' has no geometry.")
            : ReadBlobValue(reader.GetValue(8));
        pXMin.Value = reader.IsDBNull(9) ? throw new InvalidDataException($"Overture country row '{id}' has no xmin.") : reader.GetDouble(9);
        pYMin.Value = reader.IsDBNull(10) ? throw new InvalidDataException($"Overture country row '{id}' has no ymin.") : reader.GetDouble(10);
        pXMax.Value = reader.IsDBNull(11) ? throw new InvalidDataException($"Overture country row '{id}' has no xmax.") : reader.GetDouble(11);
        pYMax.Value = reader.IsDBNull(12) ? throw new InvalidDataException($"Overture country row '{id}' has no ymax.") : reader.GetDouble(12);
        insert.ExecuteNonQuery();
        rowCount++;
    }

    tx.Commit();

    using var compact = sqlite.CreateCommand();
    compact.CommandText = "VACUUM;";
    compact.ExecuteNonQuery();

    var statistics = BundledCountryArtifactValidator.Validate(sqlitePath, identityCatalog);
    Console.WriteLine(
        $"Validated {statistics.StandardIdentityCount:N0} standard identities and {statistics.WkbBytes:N0} WKB bytes.");
    return rowCount;
}

static string GetLatestRelease()
{
    try
    {
        using var conn = new DuckDBConnection("Data Source=:memory:");
        conn.Open();
        OvertureDataAccess.LoadHttpfs(conn);

        using var query = conn.CreateCommand();
            query.CommandText = $"SELECT latest FROM '{OverturePlacesLogic.LatestCatalogUrl}'";
            return query.ExecuteScalar()?.ToString() ?? OverturePlacesLogic.DocumentedFallbackRelease;
        }
        catch
        {
            return OverturePlacesLogic.DocumentedFallbackRelease;
        }
    }

static byte[] ReadBlobValue(object value)
{
    if (value is byte[] bytes)
    {
        return bytes;
    }

    if (value is Stream stream)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    throw new InvalidCastException($"Unsupported blob value type '{value.GetType().FullName}'.");
}

file sealed record ExportOptions
{
    public const string DefaultOutputRelativePath = "src/ImmichReverseGeo.Web/bundled-data/defaults/overture-airports.db";
    public const string DefaultCountryDivisionsOutputRelativePath = "src/ImmichReverseGeo.Web/bundled-data/defaults/overture-country-divisions.db";

    public string OutputPath { get; init; } = DefaultOutputRelativePath;
    public string? Release { get; init; }
    public ExportDataset Dataset { get; init; } = ExportDataset.Airports;

    public static ExportOptions Parse(string[] args)
    {
        var options = new ExportOptions();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--output":
                    options = options with { OutputPath = RequireValue(args, ref i, "--output") };
                    break;
                case "--release":
                    options = options with { Release = RequireValue(args, ref i, "--release") };
                    break;
                case "--dataset":
                    options = ParseDataset(options, RequireValue(args, ref i, "--dataset"));
                    break;
                case "--help":
                case "-h":
                    PrintHelp();
                    Environment.Exit(0);
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{args[i]}'. Use --help for usage.");
            }
        }

        return options;
    }

    private static ExportOptions ParseDataset(ExportOptions options, string value)
    {
        return value.ToLowerInvariant() switch
        {
            "airports" => options with
            {
                Dataset = ExportDataset.Airports,
                OutputPath = options.OutputPath == DefaultCountryDivisionsOutputRelativePath ? DefaultOutputRelativePath : options.OutputPath
            },
            "country-divisions" => options with
            {
                Dataset = ExportDataset.CountryDivisions,
                OutputPath = options.OutputPath == DefaultOutputRelativePath ? DefaultCountryDivisionsOutputRelativePath : options.OutputPath
            },
            _ => throw new ArgumentException($"Unknown dataset '{value}'. Use 'airports' or 'country-divisions'.")
        };
    }

    private static string RequireValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing value for {option}.");
        }

        index++;
        return args[index];
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run --project src/ImmichReverseGeo.Tools.OvertureExport [--dataset <airports|country-divisions>] [--output <path>] [--release <release>]");
        Console.WriteLine();
        Console.WriteLine($"Default airports output: {DefaultOutputRelativePath}");
        Console.WriteLine($"Default country-divisions output: {DefaultCountryDivisionsOutputRelativePath}");
    }
}

file enum ExportDataset
{
    Airports,
    CountryDivisions
}
