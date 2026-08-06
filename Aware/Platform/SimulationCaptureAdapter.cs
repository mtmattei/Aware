using System.Runtime.CompilerServices;
using Aware.Domain;
using Aware.Infrastructure;

namespace Aware.Platform;

/// <summary>
/// Tier 1 of the three capability tiers in the README: the full experience driven
/// by a seeded model. Camera and depth adapters replace only this class.
/// </summary>
public sealed class SimulationCaptureAdapter : ISpatialCaptureAdapter
{
    public Task<SpatialCaptureCapabilities> GetCapabilitiesAsync(CancellationToken ct) =>
        Task.FromResult(new SpatialCaptureCapabilities(
            HighestCapability: SpatialCaptureCapability.Simulation,
            HasCamera: false,
            HasMotion: false,
            HasDepth: false,
            HasUwb: false));

    public async IAsyncEnumerable<SpatialObservation> CaptureAsync(
        SpatialCaptureRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var room = SampleGarageFactory.Create();
        var now = DateTimeOffset.Now;

        // Replay the seeded room as if it were being observed stage by stage,
        // so downstream assembly code sees the same shape it will see from a
        // real sensor adapter.
        for (var stage = 0; stage <= 3; stage++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromMilliseconds(700), ct);

            var geometry = room.Shell
                .Concat(room.Objects.SelectMany(o => o.Primitives))
                .Where(p => p.ReconstructionStage == stage)
                .Select(p => new ObservedPrimitive(p.Kind, p.Size, p.Transform, .9f))
                .ToArray();

            var candidates = room.Objects
                .Where(o => o.Primitives.Any(p => p.ReconstructionStage == stage))
                .Select(o => new ObservedObjectCandidate(
                    o.SemanticClass,
                    SpatialTransform.At(o.Centroid),
                    o.Confidence))
                .ToArray();

            yield return new SpatialObservation(
                now.AddMilliseconds(stage * 700),
                Source: "simulation",
                Quality: .25f * (stage + 1),
                geometry,
                candidates);
        }
    }
}
