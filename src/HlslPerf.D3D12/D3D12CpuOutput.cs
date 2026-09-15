using System.Diagnostics;
using HlslPerf.Core;
using Vortice.Direct3D12;

namespace HlslPerf.D3D12;

public sealed record UnifiedCpuOutputTiming(double CpuCompletedMilliseconds, double CpuRecordMilliseconds,
    UnifiedSubmissionTiming Submission, double CpuCopyMilliseconds, double GpuRenderMilliseconds,
    double GpuReadbackMilliseconds, int ReadbackBytes);

public sealed partial class D3D12Tuner
{
    public sealed partial class UnifiedSession
    {
        /// <summary>Reuse a full-output readback resource. Dispose the reader before
        /// its session. Calls are serial on the owning tuner, as with other session APIs.</summary>
        public CpuOutputReader CreateCpuOutputReader(string resource) => new(this, resource);

        public sealed class CpuOutputReader : IDisposable
        {
            private readonly UnifiedSession session;
            private readonly string name;
            private readonly ID3D12Resource readback;
            private readonly byte[] data;
            private readonly Dictionary<string, UnifiedPass> namedPasses;
            private bool disposed;
            // Contents remain valid until the next Execute call on this reader.
            public ReadOnlyMemory<byte> Data => data;
            public ulong CommittedReadbackBytes { get; }

            internal CpuOutputReader(UnifiedSession session, string name)
            {
                this.session = session; this.name = name;
                namedPasses = session.plan.Passes.ToDictionary(p => p.Name, StringComparer.Ordinal);
                int bytes = session.resources.Get(name).ByteLength;
                data = new byte[bytes];
                var description = ResourceDescription.Buffer((ulong)bytes, ResourceFlags.None, 0);
                CommittedReadbackBytes = session.owner.device.GetResourceAllocationInfo(0, [description]).SizeInBytes;
                readback = session.owner.device.CreateCommittedResource(HeapType.Readback, description, ResourceStates.CopyDest, null);
            }

            public UnifiedCpuOutputTiming Execute(IReadOnlyDictionary<string, uint[]>? constantOverrides = null)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                ObjectDisposedException.ThrowIf(session.owner.disposed, session.owner);
                long started = Stopwatch.GetTimestamp();
                if (constantOverrides is not null)
                    foreach (var pair in constantOverrides)
                    {
                        if (!namedPasses.TryGetValue(pair.Key, out var pass) || pass.ShaderId is null || pair.Value.Length != pass.Constants.Count || pair.Value.Length > 8)
                            throw new ArgumentException("A constant override must match an existing shader pass.");
                    }
                session.Stamp(0);
                foreach (var pass in session.plan.Passes)
                    session.Execute(constantOverrides is not null && constantOverrides.TryGetValue(pass.Name, out var constants)
                        ? pass with { Constants = constants } : pass);
                session.Stamp(1);
                var output = session.resources.Get(name);
                session.owner.Transition(output, ResourceStates.CopySource);
                session.owner.commandList.CopyResource(readback, output.Resource);
                session.Stamp(2);
                double record = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                ulong[] stamps = session.Resolve(3, out var submission);
                long copyStart = Stopwatch.GetTimestamp();
                readback.Map<byte>(0, data.Length).CopyTo(data); readback.Unmap(0);
                double copy = Stopwatch.GetElapsedTime(copyStart).TotalMilliseconds;
                return new(Stopwatch.GetElapsedTime(started).TotalMilliseconds, record, submission, copy,
                    session.Elapsed(stamps, 0, 1), session.Elapsed(stamps, 1, 2), data.Length);
            }

            public void Dispose()
            {
                if (disposed) return;
                disposed = true; readback.Dispose();
            }
        }
    }
}
