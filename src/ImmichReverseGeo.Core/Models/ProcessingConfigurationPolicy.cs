using System;

namespace ImmichReverseGeo.Core.Models;

public static class ProcessingConfigurationPolicy
{
    public static string? GetBatchSizeError(int batchSize) =>
        batchSize > 0 ? null : "Batch Size must be positive.";

    public static void ValidateBatchSize(int batchSize)
    {
        if (GetBatchSizeError(batchSize) is { } error)
        {
            throw new InvalidOperationException(error);
        }
    }
}
