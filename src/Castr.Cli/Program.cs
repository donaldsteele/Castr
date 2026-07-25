using Castr.Cli;
using Castr.Core.Diagnostics; // BENCH (temporary M7 instrumentation)

try
{
    return await CastrCli.BuildRootCommand().Parse(args).InvokeAsync();
}
finally
{
    BenchMetrics.Flush(); // BENCH — no-op unless CASTR_BENCH is set
}
