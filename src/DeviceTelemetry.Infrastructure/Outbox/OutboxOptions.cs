using System.ComponentModel.DataAnnotations;

namespace DeviceTelemetry.Infrastructure.Outbox;

public sealed class OutboxOptions : IValidatableObject
{
    public const string SectionName = "Outbox";

    /// <summary>Maximum number of messages a worker claims (and then sends one by one) per poll.</summary>
    [Range(1, 1000)]
    public int BatchSize { get; set; } = 20;

    /// <summary>Delay between polls when the previous poll did not fill a whole batch.</summary>
    [Range(typeof(TimeSpan), "00:00:00.100", "01:00:00")]
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Total delivery attempts before a message is marked <c>Failed</c>.</summary>
    [Range(1, 100)]
    public int MaxAttempts { get; set; } = 8;

    /// <summary>Delay after the first failure; doubled after each further failure, capped at <see cref="MaxRetryDelay"/>.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00")]
    public TimeSpan BaseRetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    [Range(typeof(TimeSpan), "00:00:01", "7.00:00:00")]
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// How long a claimed message stays reserved for one worker. A message whose worker crashed is retried after
    /// the lease expires, so it must comfortably exceed the time to send one batch.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:10", "1.00:00:00")]
    public TimeSpan LockDuration { get; set; } = TimeSpan.FromMinutes(5);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (MaxRetryDelay < BaseRetryDelay)
            yield return new ValidationResult("Outbox:MaxRetryDelay must not be less than Outbox:BaseRetryDelay.", [nameof(MaxRetryDelay)]);
    }
}
