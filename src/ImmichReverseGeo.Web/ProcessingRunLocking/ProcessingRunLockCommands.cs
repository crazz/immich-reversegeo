using NpgsqlTypes;

namespace ImmichReverseGeo.Web.ProcessingRunLocking;

internal static class ProcessingRunLockIdentity
{
    internal const int KeyVersion = 1;
    internal const string DerivationLabel = "immich-reversegeo/postgresql-advisory-run-lock/v1";
    internal const string Sha256 = "916360a3f80ad7d0ae2a32661692f1381e43b2f336f19a58491ce5582ffb9dbf";
    internal const long Key = -7970420658158250032L;
}

internal readonly record struct ProcessingRunLockCommand(
    string CommandText,
    NpgsqlDbType? ParameterType,
    object? ParameterValue)
{
    internal static ProcessingRunLockCommand Acquire { get; } = new(
        "SELECT pg_try_advisory_lock($1)",
        NpgsqlDbType.Bigint,
        ProcessingRunLockIdentity.Key);

    internal static ProcessingRunLockCommand Probe { get; } = new(
        "SELECT 1",
        null,
        null);

    internal static ProcessingRunLockCommand Release { get; } = new(
        "SELECT pg_advisory_unlock($1)",
        NpgsqlDbType.Bigint,
        ProcessingRunLockIdentity.Key);
}
