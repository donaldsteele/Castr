using Castr.Core.Trust;

namespace Castr.Gui.Trust;

/// <summary>
/// A non-interactive <see cref="ITrustPrompt"/> that resolves to a fixed answer without any UI. Used for
/// design-time/default composition (where popping a real dialog is undesirable) and by tests. Like every
/// well-behaved trust prompt it never throws.
/// </summary>
public sealed class AutoTrustPrompt(bool answer) : ITrustPrompt
{
    public int CallCount { get; private set; }

    public Task<bool> RequestTrustAsync(TrustPromptContext context, CancellationToken cancellationToken)
    {
        CallCount++;
        return Task.FromResult(answer);
    }
}
