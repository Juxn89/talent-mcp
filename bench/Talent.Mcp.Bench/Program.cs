// Entry point for the benchmark harness.
//
// Every target is a pure function over Talent.Domain — no repository, no DbContext, no server, no
// Docker. That is the same property that lets the domain tests run in milliseconds, and it is why
// these numbers are reproducible on any machine rather than describing one developer's Postgres.
//
// Run with:  dotnet run --project bench/Talent.Mcp.Bench -c Release
// BenchmarkDotNet refuses to run a Debug build rather than reporting misleading numbers.
using BenchmarkDotNet.Running;

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

/// <summary>Marker for <see cref="BenchmarkSwitcher.FromAssembly"/>.</summary>
public partial class Program;
