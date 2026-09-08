using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Core.WorkerProcessExitOutcomes;
using ImmichReverseGeo.Gadm.Models;
using ImmichReverseGeo.Gadm.Services;
using ImmichReverseGeo.Overture.Models;
using ImmichReverseGeo.Overture.Services;
using ImmichReverseGeo.Web.Composition;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.WorkerHost;
using ImmichReverseGeo.Web.WorkerHost.WorkerNdjsonOutput;
using ImmichReverseGeo.Web.WorkerHost.WorkerStdinRequestLoop;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using System.Text;

namespace ImmichReverseGeo.WorkerProcessFixture;

internal static class ProductionCoordinateHostFixture
{
    internal static async Task<int> RunAsync(
        FixtureOptions options,
        InternalWorkerProtocolVersion protocolVersion)
    {
        if (protocolVersion != InternalWorkerProtocolVersion.V2)
        {
            throw new FixtureInputException("Production CoordinateLookup fixtures require protocol v2.");
        }

        var outcomes = new WorkerProcessExitOutcomeAccumulator();
        ApplicationCompositionContext context = ApplicationCompositionContext.Create(
            CompositionEnvironment.Development,
            options.ResourceRoot,
            options.ResourceRoot,
            options.ResourceRoot);
        var builder = InternalWorkerHost.CreateBuilder(context, outcomes, protocolVersion);
        builder.Services.RemoveAll<ICoordinateLookupSources>();
        builder.Services.AddSingleton<ICoordinateLookupSources>(
            new FixtureCoordinateSources(options));
        AddForbiddenPersistenceSentinels(builder.Services, options.ResourceRoot);

        if (options.Scenario == FixtureScenario.RealCoordinateOutputFailure)
        {
            var outputFailure = new OutputFailureSynchronization(options.ResourceRoot);
            builder.Services.RemoveAll<IWorkerStandardInputStreamFactory>();
            builder.Services.AddSingleton<IWorkerStandardInputStreamFactory>(
                new ObservedStandardInputFactory(outputFailure));
            builder.Services.RemoveAll<IWorkerNdjsonOutputStreamFactory>();
            builder.Services.AddSingleton<IWorkerNdjsonOutputStreamFactory>(
                new FailAfterReadyOutputFactory(options.ResourceRoot, outputFailure));
        }

        if (options.Scenario == FixtureScenario.RealCoordinateStartupFailure)
        {
            builder.Services.RemoveAll<IWorkerReadinessPublisher>();
            builder.Services.AddSingleton<IWorkerReadinessPublisher>(
                new FailingReadinessPublisher(options.ResourceRoot));
        }

        return await InternalWorkerHost.RunHostAsync(
            builder.Build(),
            outcomes).ConfigureAwait(false);
    }

    private static void AddForbiddenPersistenceSentinels(
        IServiceCollection services,
        string root)
    {
        services.RemoveAll<IProcessingAssetRepository>();
        services.AddSingleton<IProcessingAssetRepository>(_ =>
            ForbiddenPersistence<IProcessingAssetRepository>(root));
        services.RemoveAll<ImmichDbRepository>();
        services.AddSingleton<ImmichDbRepository>(_ =>
            ForbiddenPersistence<ImmichDbRepository>(root));
        services.RemoveAll<NpgsqlDataSource>();
        services.AddSingleton<NpgsqlDataSource>(_ =>
            ForbiddenPersistence<NpgsqlDataSource>(root));
    }

    private static T ForbiddenPersistence<T>(string root)
    {
        File.WriteAllText(Path.Combine(root, "persistence-accessed.marker"), typeof(T).Name);
        throw new InvalidOperationException("CoordinateLookup must not resolve persistence services.");
    }

    private sealed class FixtureCoordinateSources(FixtureOptions options) : ICoordinateLookupSources
    {
        public Task<BundledCountryLookupResult> FindCountryAsync(
            double latitude,
            double longitude,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Mark("source-called.marker");
            return options.Scenario switch
            {
                FixtureScenario.RealCoordinateNoCountry =>
                    Task.FromResult(BundledCountryLookupResult.SpatialNoMatch("fixture no match")),
                FixtureScenario.RealCoordinateDomainFailure => DomainFailure(),
                _ => Task.FromResult(BundledCountryLookupResult.Matched(
                    "USA",
                    "United States",
                    "US",
                    "fixture-country"))
            };
        }

        public CityResolverProfile ResolveCityProfile(
            CoordinateLookupCityResolverOverrides overrides,
            string? iso3) =>
            new CityResolverProfileCatalog().GetProfile(
                CoordinateLookupCityProfileConversions.ToConfig(overrides),
                iso3);

        public IReadOnlyList<string> ExpandGadmCandidateCodes(string iso3) =>
            GadmCountryFallbackCatalog.ExpandCandidateCodes(iso3);

        public (Task Task, OvertureDivisionEnsureResult Result) GetOrStartOvertureCache(
            string iso3,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Mark("overture-cache-called.marker");
            return options.Scenario switch
            {
                FixtureScenario.RealCoordinateCancellation => (
                    ObserveCancellationAsync(cancellationToken),
                    OvertureDivisionEnsureResult.StartedDownload),
                FixtureScenario.RealCoordinateDegraded => (
                    Task.FromException(new InvalidOperationException("fixture source unavailable")),
                    OvertureDivisionEnsureResult.StartedDownload),
                _ => (Task.CompletedTask, OvertureDivisionEnsureResult.AlreadyReady)
            };
        }

        public bool HasOvertureCache(string iso3) =>
            options.Scenario != FixtureScenario.RealCoordinateDegraded;

        public Task<OvertureDivisionLookupDiagnostics> FindOvertureDivisionsAsync(
            double latitude,
            double longitude,
            string alpha2,
            string iso3,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Mark("overture-division-called.marker");
            var best = new OvertureDivisionResult(
                "fixture-division",
                "Fixture City",
                "locality",
                "land",
                8,
                alpha2,
                iso3,
                true,
                false,
                true,
                true,
                true,
                1);
            return Task.FromResult(new OvertureDivisionLookupDiagnostics(
                best,
                [
                    new OvertureDivisionCandidateDiagnostic(
                        best.Id,
                        best.Name,
                        best.SubType,
                        best.ClassName,
                        best.AdminLevel,
                        best.Country,
                        best.IsLand,
                        best.IsTerritorial,
                        best.BoundingBoxContainsPoint,
                        best.GeometryContainsPoint,
                        best.BoundingBoxArea,
                        true,
                        "selected by fixture profile")
                ],
                "fixture-release"));
        }

        public (Task Task, GadmDivisionEnsureResult Result) GetOrStartGadmCache(
            string iso3,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Mark("gadm-cache-called.marker");
            return (Task.CompletedTask, GadmDivisionEnsureResult.AlreadyReady);
        }

        public bool HasGadmCache(string iso3) => true;

        public Task<GadmDivisionLookupDiagnostics> FindGadmDivisionsAsync(
            double latitude,
            double longitude,
            IReadOnlyList<string> iso3Codes,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Mark("gadm-division-called.marker");
            var best = new GadmDivisionResult(
                "fixture-gadm",
                "Fixture State",
                "State",
                "State",
                1,
                true,
                true,
                2);
            return Task.FromResult(new GadmDivisionLookupDiagnostics(
                best,
                [
                    new GadmDivisionCandidateDiagnostic(
                        best.Id,
                        best.Name,
                        best.EnglishType,
                        best.LocalType,
                        best.AdminLevel,
                        best.BoundingBoxContainsPoint,
                        best.GeometryContainsPoint,
                        best.BoundingBoxArea,
                        true,
                        "selected by fixture profile")
                ],
                GadmDivisionsLogic.DatasetVersion));
        }

        public Task<OvertureInfrastructureLookupDiagnostics> FindAirportAsync(
            double latitude,
            double longitude,
            string iso3,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Mark("airport-called.marker");
            var best = new OvertureInfrastructureResult(
                "fixture-airport",
                "Fixture Airport",
                "infrastructure",
                "airport",
                "airport",
                100,
                true,
                true,
                ["fixture-source"]);
            return Task.FromResult(new OvertureInfrastructureLookupDiagnostics(
                best,
                [
                    new OvertureInfrastructureCandidateDiagnostic(
                        best.Id,
                        best.Name,
                        best.FeatureType,
                        best.SubType,
                        best.ClassName,
                        best.DistanceMetres,
                        best.BoundingBoxContainsPoint,
                        best.GeometryContainsPoint,
                        best.Sources,
                        true,
                        "geometry contains coordinate")
                ],
                "fixture-release"));
        }

        public Task<OvertureLookupDiagnostics> FindPlacesAsync(
            double latitude,
            double longitude,
            string alpha2,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Mark("places-called.marker");
            var best = new OverturePlaceResult(
                "fixture-place",
                "Fixture Place",
                "restaurant",
                "food_and_drink",
                0.95,
                "open",
                25,
                true,
                ["fixture-source"]);
            return Task.FromResult(new OvertureLookupDiagnostics(
                best,
                [
                    new OvertureCandidateDiagnostic(
                        best.Id,
                        best.Name,
                        best.Category,
                        best.BasicCategory,
                        best.Confidence,
                        best.OperatingStatus,
                        best.DistanceMetres,
                        best.BoundingBoxContainsPoint,
                        best.Sources,
                        true,
                        "nearest diagnostic place")
                ],
                "fixture-release",
                alpha2));
        }

        private Task<BundledCountryLookupResult> DomainFailure()
        {
            Mark("domain-fault-injected.marker");
            return Task.FromException<BundledCountryLookupResult>(
                new InvalidOperationException("fixture country failure"));
        }

        private async Task ObserveCancellationAsync(CancellationToken cancellationToken)
        {
            Mark("owned-cache-started.marker");
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Mark("owned-cache-cancelled.marker");
                throw;
            }
        }

        private void Mark(string name) =>
            File.WriteAllText(Path.Combine(options.ResourceRoot, name), "reached");
    }

    private sealed class FailingReadinessPublisher(string root) : IWorkerReadinessPublisher
    {
        public Task PublishAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.WriteAllText(Path.Combine(root, "startup-fault-injected.marker"), "reached");
            return Task.FromException(new InvalidOperationException("fixture readiness failure"));
        }
    }

    private sealed class FailAfterReadyOutputFactory(
        string root,
        OutputFailureSynchronization synchronization) : IWorkerNdjsonOutputStreamFactory
    {
        public Stream OpenStandardOutput() =>
            new FailAfterFirstFrameStream(
                new WorkerNdjsonStandardOutputStreamFactory().OpenStandardOutput(),
                root,
                synchronization);
    }

    private sealed class FailAfterFirstFrameStream(
        Stream inner,
        string root,
        OutputFailureSynchronization synchronization) : Stream
    {
        private int _writes;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) =>
            inner.FlushAsync(cancellationToken);

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _writes) > 1)
            {
                await synchronization.PostExecuteReadPending.Task
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
                string frame = Encoding.UTF8.GetString(buffer.Span);
                string reached = frame.Contains(
                        "\"type\":\"job-started\"",
                        StringComparison.Ordinal)
                    && frame.Contains(
                        "\"jobKind\":\"CoordinateLookup\"",
                        StringComparison.Ordinal)
                        ? "coordinate-job-started"
                        : "unexpected-second-write";
                File.WriteAllText(Path.Combine(root, "output-fault-injected.marker"), reached);
                throw new IOException("fixture output failure");
            }

            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            synchronization.MarkReadyWritten();
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

    }

    private sealed class ObservedStandardInputFactory(
        OutputFailureSynchronization synchronization) : IWorkerStandardInputStreamFactory
    {
        public Stream OpenStandardInput() =>
            new ObservedStandardInputStream(
                new WorkerStandardInputStreamFactory().OpenStandardInput(),
                synchronization);
    }

    private sealed class ObservedStandardInputStream(
        Stream inner,
        OutputFailureSynchronization synchronization) : Stream
    {
        private bool _executeFrameRead;
        private bool _postExecuteReadObserved;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ValueTask<int> pendingRead = inner.ReadAsync(buffer, cancellationToken);
            bool observe = _executeFrameRead
                && !_postExecuteReadObserved
                && !pendingRead.IsCompleted;
            if (observe)
            {
                _postExecuteReadObserved = true;
                synchronization.MarkPostExecuteReadPending();
            }

            try
            {
                int count = await pendingRead.ConfigureAwait(false);
                if (synchronization.ReadyWritten
                    && buffer.Span[..count].Contains((byte)'\n'))
                {
                    _executeFrameRead = true;
                }

                return count;
            }
            catch (OperationCanceledException) when (observe && cancellationToken.IsCancellationRequested)
            {
                synchronization.MarkPostExecuteReadCancelled();
                throw;
            }
            finally
            {
                if (observe)
                {
                    synchronization.MarkPostExecuteReadFinished();
                }
            }
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            inner.Read(buffer, offset, count);
        public override void Flush() => throw new NotSupportedException();
        public override int ReadByte() => inner.ReadByte();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class OutputFailureSynchronization(string root)
    {
        private int _readyWritten;
        internal bool ReadyWritten => Volatile.Read(ref _readyWritten) != 0;
        internal TaskCompletionSource PostExecuteReadPending { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void MarkReadyWritten() => Interlocked.Exchange(ref _readyWritten, 1);

        internal void MarkPostExecuteReadPending()
        {
            File.WriteAllText(Path.Combine(root, "input-post-execute-read-pending.marker"), "reached");
            PostExecuteReadPending.TrySetResult();
        }

        internal void MarkPostExecuteReadCancelled() =>
            File.WriteAllText(Path.Combine(root, "input-post-execute-read-cancelled.marker"), "reached");

        internal void MarkPostExecuteReadFinished() =>
            File.WriteAllText(Path.Combine(root, "input-post-execute-read-finished.marker"), "reached");
    }
}
