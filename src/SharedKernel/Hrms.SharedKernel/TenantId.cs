namespace Hrms.SharedKernel;

/// <summary>Strongly typed tenant identifier. Never empty: an empty tenant would match rows that belong to nobody.</summary>
public readonly record struct TenantId
{
    public TenantId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A tenant id cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public static bool TryParse(string? text, out TenantId tenantId)
    {
        if (Guid.TryParse(text, out var guid) && guid != Guid.Empty)
        {
            tenantId = new TenantId(guid);
            return true;
        }

        tenantId = default;
        return false;
    }

    public override string ToString() => Value.ToString("D");
}
