namespace Talent.Mcp.Server.Stdio;

using System.Diagnostics;
using System.Globalization;

/// <summary>
/// Emits machine-readable startup phase markers so a benchmark can attribute cold start to the
/// things that actually cause it.
/// <para>
/// This host is launched once per client session, so cold start is the metric that matters — but a
/// naive "spawn to exit" measurement of it would mostly report Postgres. Two blocking database
/// interactions happen before the transport is serving: <c>CreateAndPrepareTaskStoreAsync</c> opens a
/// connection and applies the task-store DDL, and <c>StartAsync</c> opens a second one and waits for
/// its first <c>LISTEN</c>. Marking both means the database share can be reported separately and
/// subtracted, instead of being silently folded into "runtime init".
/// </para>
/// <para>
/// The alternative — an environment variable that skips the database — was rejected. It would
/// measure a process that cannot answer five of the six tools, which is precisely the mistake
/// <see href="../../docs/adr/0002-native-aot-and-explicit-tool-registration.md">ADR-0002</see>
/// records: the thing measured was not the thing shipped. It would also add a production code path
/// whose only consumer is the benchmark.
/// </para>
/// <para>
/// <strong>stderr, never stdout.</strong> stdout is the JSON-RPC transport; writing a marker there
/// would corrupt the stream exactly as the console logger does. Gated behind an environment variable
/// so a client that surfaces stderr to its user never sees benchmark noise.
/// </para>
/// </summary>
internal static class StartupTrace
{
    /// <summary>Set to <c>1</c> or <c>true</c> to emit markers.</summary>
    internal const string EnableVariable = "TALENT_STARTUP_TRACE";

    /// <summary>Line prefix, so a harness can pick markers out of ordinary stderr logging.</summary>
    private const string Prefix = "talent.startup";

    private static readonly long StartedAtTicks = Stopwatch.GetTimestamp();

    /// <summary>Whether markers are being emitted. Read once, at type initialization.</summary>
    internal static bool Enabled { get; } =
        Environment.GetEnvironmentVariable(EnableVariable) is "1" or "true" or "TRUE";

    /// <summary>Emits one phase marker with the elapsed time since the first call.</summary>
    /// <param name="phase">Short phase name, e.g. <c>entry</c>.</param>
    internal static void Mark(string phase)
    {
        if (!Enabled)
        {
            return;
        }

        Write(phase, elapsedOnly: true);
    }

    /// <summary>
    /// Emits the final marker, carrying the memory and assembly counts alongside the elapsed time.
    /// <para>
    /// <c>assemblies</c> is the one figure here with no measurement noise at all, and the one that
    /// actually explains what trimming did. Peak working set is deliberately NOT reported from
    /// inside the process: a process cannot reliably observe its own peak, and the peak may well
    /// occur after this point. The harness takes that externally.
    /// </para>
    /// </summary>
    /// <param name="phase">Short phase name, e.g. <c>ready</c>.</param>
    internal static void MarkReady(string phase)
    {
        if (!Enabled)
        {
            return;
        }

        Write(phase, elapsedOnly: false);
    }

    private static void Write(string phase, bool elapsedOnly)
    {
        var elapsed = Stopwatch.GetElapsedTime(StartedAtTicks).TotalMilliseconds;
        var line = string.Create(CultureInfo.InvariantCulture, $"{Prefix} phase={phase} elapsed_ms={elapsed:F3}");

        if (!elapsedOnly)
        {
            var heap = GC.GetGCMemoryInfo().HeapSizeBytes;
            var allocated = GC.GetTotalAllocatedBytes(precise: false);
            var assemblies = AppDomain.CurrentDomain.GetAssemblies().Length;

            line += string.Create(
                CultureInfo.InvariantCulture,
                $" heap_bytes={heap} allocated_bytes={allocated} assemblies={assemblies} working_set_bytes={Environment.WorkingSet}");
        }

        Console.Error.WriteLine(line);
        Console.Error.Flush();
    }
}
