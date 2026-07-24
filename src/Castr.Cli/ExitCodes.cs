namespace Castr.Cli;

/// <summary>
/// Process exit codes, chosen so scripts/CI can distinguish outcomes. 0 = success; everything else is a
/// distinct failure mode. (System.CommandLine itself returns 1 for argument-parse/validation errors, so we
/// deliberately reserve 1 for that and start our own runtime failures at 2.)
/// </summary>
internal static class ExitCodes
{
    public const int Success = 0;
    public const int UsageError = 1;      // reserved for System.CommandLine parse/validation failures
    public const int RuntimeError = 2;    // unexpected exception during send/receive
    public const int TrustDenied = 3;     // receiver: sender was not trusted and no prompt accepted it
    public const int Incomplete = 4;      // receiver: cancelled/stopped before the transfer completed
    public const int InvalidInput = 5;    // bad file path, unresolvable interface, malformed key id, etc.
}
