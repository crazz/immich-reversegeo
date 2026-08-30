namespace ImmichReverseGeo.Core.Processing;

/// <summary>
/// Defines the block-8 diagnostic vocabulary for later adapters without routing any runtime diagnostics.
/// </summary>
public static class ProcessingEventDiagnosticVocabulary
{
    public const ProcessingLogLevel ResolvedLocationDetailLevel = ProcessingLogLevel.Trace;
    public const ProcessingLogLevel ExistingUiWarningLevel = ProcessingLogLevel.Warning;
    public const ProcessingLogLevel ExistingUiErrorLevel = ProcessingLogLevel.Error;

    /// <summary>ILogger-only diagnostics, including the current no-city diagnostic, have no event mapping.</summary>
    public const bool LoggerOnlyDiagnosticsProduceEvents = false;
}
