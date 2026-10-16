using System.ComponentModel.DataAnnotations;

namespace DeviceTelemetry.Infrastructure.Persistence.Seeding;

public sealed class StagingSeedOptions
{
    public const string SectionName = "StagingSeed";

    /// <summary>
    /// Real mailbox that receives the Staging notifications. When set, seeded customer N gets the plus-address
    /// <c>local+customerNN@domain</c> (delivered to this mailbox by Gmail and most providers); otherwise
    /// customers get undeliverable <c>customerNN@example.com</c> addresses.
    /// </summary>
    [EmailAddress]
    public string? RecipientEmail { get; set; }

    public string CustomerEmail(int customerNumber)
    {
        var suffix = $"customer{customerNumber:00}";
        if (string.IsNullOrWhiteSpace(RecipientEmail))
            return $"{suffix}@example.com";

        var at = RecipientEmail.LastIndexOf('@');
        return $"{RecipientEmail[..at]}+{suffix}{RecipientEmail[at..]}";
    }
}
