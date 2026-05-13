using Workable;

namespace Workable.SampleHost.Operations;

public sealed record WelcomeEmailInput(
    string Email,
    string DisplayName,
    EmailPriority Priority = EmailPriority.Normal,
    bool SendCopyToAccountOwner = false,
    string? CouponCode = null);

public sealed record WelcomeEmailOutput(
    string MessageId,
    string Recipient,
    EmailPriority Priority,
    DateTimeOffset AcceptedAt);

public enum EmailPriority
{
    Low,
    Normal,
    High,
}

[WorkMetadata("email.welcome.send", "Communications:Email", "Sends a welcome email with optional campaign details.")]
public sealed class WelcomeEmailWork : IWorkExecutor<WelcomeEmailInput, WelcomeEmailOutput>
{
    public Task<WorkExecutionResult<WelcomeEmailOutput>> Execute(
        IWorkExecutionContext context,
        WelcomeEmailInput input,
        CancellationToken cancellationToken)
        => Task.FromResult(WorkExecutionResult<WelcomeEmailOutput>.Success(
            new WelcomeEmailOutput(
                $"msg_{Guid.NewGuid():N}"[..16],
                input.Email,
                input.Priority,
                DateTimeOffset.UtcNow),
            [WorkMessage.Info("email.accepted", $"Queued welcome email for {input.DisplayName}.")]));
}
