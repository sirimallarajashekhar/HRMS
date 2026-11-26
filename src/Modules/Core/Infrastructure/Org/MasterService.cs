using System.Linq.Expressions;
using Hrms.Contracts;
using Hrms.Core.Application.Org;
using Hrms.Core.Domain.Org;
using Hrms.Core.Infrastructure.Persistence;
using Hrms.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Hrms.Core.Infrastructure.Org;

/// <summary>
/// The shared create / read / update / soft-delete flow of the coded masters. Everything goes through <see cref="CoreDbContext"/>, so
/// the tenant filter applies to every lookup: another tenant's id is simply not found. Updates carry the row version they were based on.
/// </summary>
internal abstract class MasterService<TEntity, TCreate, TUpdate, TView>(CoreDbContext db, IEnumerable<IMasterUsageCheck> usageChecks)
    : IMasterService<TCreate, TUpdate, TView>
    where TEntity : CodedMaster
    where TUpdate : IUpdateRequest
{
    protected CoreDbContext Db { get; } = db;

    /// <summary>The master's name in error codes (<c>core.&lt;master&gt;.not_found</c>) and in usage checks.</summary>
    protected abstract string Master { get; }

    private Func<TEntity, TView>? _compiledView;

    /// <summary>How a row becomes its view; written once as an expression so list queries project in SQL and writes reuse it in memory.</summary>
    protected abstract Expression<Func<TEntity, TView>> View { get; }

    private Func<TEntity, TView> ToView => _compiledView ??= View.Compile();

    protected abstract Task<Result<TEntity>> BuildAsync(TCreate request, CancellationToken cancellationToken);

    protected abstract Task<Result> ApplyAsync(TEntity entity, TUpdate request, CancellationToken cancellationToken);

    /// <summary>Narrows a list to rows tied to a related master (type, grade, legal entity).</summary>
    protected virtual IQueryable<TEntity> Related(IQueryable<TEntity> query, Guid relatedId) => query;

    /// <summary>True when another Core table still references the row.</summary>
    protected virtual Task<bool> IsReferencedInCoreAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(false);

    public async Task<PagedResult<TView>> ListAsync(MasterQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var q = query.Normalised();

        var rows = Db.Set<TEntity>().AsNoTracking();
        if (q.Search is { } search)
        {
            rows = rows.Where(e => e.Code.Contains(search) || e.Name.Contains(search));
        }

        if (q.Active is { } active)
        {
            rows = rows.Where(e => e.IsActive == active);
        }

        if (q.RelatedId is { } related)
        {
            rows = Related(rows, related);
        }

        var total = await rows.LongCountAsync(cancellationToken);
        var items = await rows
            .OrderBy(e => e.Code)
            .Skip((q.Page - 1) * q.PageSize)
            .Take(q.PageSize)
            .Select(View)
            .ToListAsync(cancellationToken);

        return new PagedResult<TView>(items, q.Page, q.PageSize, total);
    }

    public async Task<Result<TView>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var view = await Db.Set<TEntity>().AsNoTracking()
            .Where(e => e.Id == id)
            .Select(View)
            .SingleOrDefaultAsync(cancellationToken);

        return view is null ? Result.Failure<TView>(NotFound()) : Result.Success(view);
    }

    public async Task<Result<TView>> CreateAsync(TCreate request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var built = await BuildAsync(request, cancellationToken);
        if (built.IsFailure)
        {
            return Result.Failure<TView>(built.Error);
        }

        var entity = built.Value;
        if (await CodeExistsAsync(entity.Code, cancellationToken))
        {
            return Result.Failure<TView>(CodeTaken());
        }

        Db.Set<TEntity>().Add(entity);
        try
        {
            await Db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Two creates may have raced past the check above and the unique index stopped this one; anything else is a real failure.
            if (await CodeExistsAfterFailureAsync(entity, cancellationToken))
            {
                return Result.Failure<TView>(CodeTaken());
            }

            throw;
        }

        return Result.Success(ToView(entity));
    }

    public async Task<Result<TView>> UpdateAsync(Guid id, TUpdate request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.RowVersion is not { } version)
        {
            return Result.Failure<TView>(RowVersionRequired());
        }

        var entity = await Db.Set<TEntity>().SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (entity is null)
        {
            return Result.Failure<TView>(NotFound());
        }

        if (entity.RowVersion != version)
        {
            return Result.Failure<TView>(Stale());
        }

        var applied = await ApplyAsync(entity, request, cancellationToken);
        if (applied.IsFailure)
        {
            return Result.Failure<TView>(applied.Error);
        }

        try
        {
            await Db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure<TView>(Stale());
        }

        return Result.Success(ToView(entity));
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var entity = await Db.Set<TEntity>().SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (entity is null)
        {
            return Result.Failure(NotFound());
        }

        if (await IsReferencedInCoreAsync(id, cancellationToken) || await IsUsedElsewhereAsync(id, cancellationToken))
        {
            return Result.Failure(Error.Conflict($"core.{Master}.in_use", "This record is still in use and cannot be deleted. Deactivate it instead."));
        }

        Db.Set<TEntity>().Remove(entity);
        try
        {
            await Db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure(Stale());
        }

        return Result.Success();
    }

    protected Error NotFound() => Error.NotFound($"core.{Master}.not_found", "The record does not exist.");

    protected Error Stale() =>
        Error.Conflict($"core.{Master}.stale_version", "The record was changed by someone else. Reload it and apply your change again.");

    private Error CodeTaken() => Error.Conflict($"core.{Master}.code_taken", "That code is already used.");

    private static Error RowVersionRequired() =>
        Error.Validation("core.row_version_required", "The rowVersion the change is based on is required.");

    private async Task<bool> IsUsedElsewhereAsync(Guid id, CancellationToken cancellationToken)
    {
        foreach (var check in usageChecks.Where(c => c.Master == Master))
        {
            if (await check.IsInUseAsync(id, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    private Task<bool> CodeExistsAsync(string code, CancellationToken cancellationToken) =>
        Db.Set<TEntity>().AsNoTracking().AnyAsync(e => e.Code == code, cancellationToken);

    private async Task<bool> CodeExistsAfterFailureAsync(TEntity failed, CancellationToken cancellationToken)
    {
        Db.Entry(failed).State = EntityState.Detached;
        return await CodeExistsAsync(failed.Code, cancellationToken);
    }
}

