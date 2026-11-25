namespace Hrms.SharedKernel;

/// <summary>Base of every persisted entity. Ids are UUIDv7: not enumerable (IDOR) yet index-friendly (time ordered).</summary>
public abstract class Entity
{
    protected Entity() => Id = Guid.CreateVersion7();

    public Guid Id { get; private set; }
}
