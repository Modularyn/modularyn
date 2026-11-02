using Xunit;

namespace Modularyn.Tooling.Samples.TeamWorkspace.Tests;

public sealed class EvidenceExecutionTests
{
    [Fact]
    public async Task A_capture_refusal_cannot_replace_the_methods_own_failure()
    {
        var failure = new InvalidOperationException("The method failed.");
        EvidenceOutcome? captured = null;
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => EvidenceExecution.RunAsync(
            () => Task.FromException(failure), outcome =>
            {
                captured = outcome;
                throw new EvidenceValidationException("capture refused");
            }));
        Assert.Same(failure, actual);
        Assert.Equal(EvidenceOutcome.Failed, captured);
    }

    [Fact]
    public async Task A_capture_refusal_after_success_still_fails_the_run()
    {
        var refusal = new EvidenceValidationException("capture refused");
        EvidenceOutcome? captured = null;
        var actual = await Assert.ThrowsAsync<EvidenceValidationException>(() => EvidenceExecution.RunAsync(
            () => Task.CompletedTask, outcome =>
            {
                captured = outcome;
                return Task.FromException(refusal);
            }));
        Assert.Same(refusal, actual);
        Assert.Equal(EvidenceOutcome.Passed, captured);
    }
}
