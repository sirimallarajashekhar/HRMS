using Hrms.Core.Domain.LegalEntities;
using Hrms.Core.Domain.Org;
using Hrms.Persistence;
using Hrms.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Hrms.Core.Infrastructure.Persistence.Configurations;

/// <summary>What every coded master shares: code and name columns, and a code that is unique per tenant among live rows.</summary>
internal static class CodedMasterMapping
{
    public static void Map<T>(EntityTypeBuilder<T> builder)
        where T : CodedMaster
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Code).HasMaxLength(CodedMaster.CodeMaxLength).IsRequired();
        builder.Property(e => e.Name).HasMaxLength(CodedMaster.NameMaxLength).IsRequired();
        builder.Property(e => e.IsActive).IsRequired();
        builder.HasUniquePerTenant(e => e.Code, CodedMaster.CodeMaxLength);
    }
}

internal sealed class OrgUnitTypeConfiguration : IEntityTypeConfiguration<OrgUnitType>
{
    public void Configure(EntityTypeBuilder<OrgUnitType> builder)
    {
        CodedMasterMapping.Map(builder);
        builder.HasIndex(nameof(TenantEntity.TenantId), nameof(OrgUnitType.Level)).HasDatabaseName("ix_org_unit_type_tenant_level");
        builder.ToTable(table => table.HasCheckConstraint(
            "ck_org_unit_type_level", $"`level` BETWEEN 1 AND {OrgUnitType.MaxLevel}"));
    }
}

internal sealed class OrgUnitConfiguration : IEntityTypeConfiguration<OrgUnit>
{
    public void Configure(EntityTypeBuilder<OrgUnit> builder)
    {
        CodedMasterMapping.Map(builder);
        builder.HasOne<OrgUnitType>().WithMany().HasForeignKey(e => e.OrgUnitTypeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(nameof(TenantEntity.TenantId), nameof(OrgUnit.OrgUnitTypeId)).HasDatabaseName("ix_org_unit_tenant_type");
    }
}

internal sealed class OrgUnitParentConfiguration : IEntityTypeConfiguration<OrgUnitParent>
{
    public void Configure(EntityTypeBuilder<OrgUnitParent> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Ignore(e => e.Period);
        builder.HasOne<OrgUnit>().WithMany().HasForeignKey(e => e.OrgUnitId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<OrgUnit>().WithMany().HasForeignKey(e => e.ParentId).OnDelete(DeleteBehavior.Restrict);

        // One row per unit and start date: the backstop that stops two racing moves from both recording the same period.
        builder.HasIndex(nameof(TenantEntity.TenantId), nameof(OrgUnitParent.OrgUnitId), nameof(OrgUnitParent.EffectiveFrom))
            .IsUnique()
            .HasDatabaseName("ux_org_unit_parent_tenant_unit_from");

        // Serves "the children of X on a date" and the whole-tree read.
        builder.HasIndex(nameof(TenantEntity.TenantId), nameof(OrgUnitParent.ParentId), nameof(OrgUnitParent.EffectiveFrom))
            .HasDatabaseName("ix_org_unit_parent_tenant_parent_from");
    }
}

internal sealed class LocationConfiguration : IEntityTypeConfiguration<Location>
{
    public void Configure(EntityTypeBuilder<Location> builder)
    {
        CodedMasterMapping.Map(builder);
        builder.Property(e => e.AddressLine1).HasMaxLength(Location.AddressMaxLength).IsRequired();
        builder.Property(e => e.AddressLine2).HasMaxLength(Location.AddressMaxLength);
        builder.Property(e => e.City).HasMaxLength(Location.CityMaxLength).IsRequired();
        builder.Property(e => e.State).HasMaxLength(Location.CityMaxLength);
        builder.Property(e => e.PostalCode).HasMaxLength(Location.PostalCodeMaxLength);
        builder.Property(e => e.CountryCode).HasMaxLength(2).IsFixedLength().IsRequired();
        builder.Property(e => e.TimeZoneId).HasMaxLength(Location.TimeZoneMaxLength).IsRequired();
        builder.Property(e => e.WorkWeekDays).IsRequired();
        builder.ToTable(table => table.HasCheckConstraint("ck_location_work_week_days", "`work_week_days` BETWEEN 1 AND 127"));
    }
}

internal sealed class GeofenceConfiguration : IEntityTypeConfiguration<Geofence>
{
    public void Configure(EntityTypeBuilder<Geofence> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Name).HasMaxLength(Geofence.NameMaxLength).IsRequired();
        builder.Property(e => e.Kind).HasConversion<int>().IsRequired();
        builder.Property(e => e.CenterLatitude).HasPrecision(9, 6);
        builder.Property(e => e.CenterLongitude).HasPrecision(9, 6);
        builder.Property(e => e.PolygonGeoJson).HasColumnType("text");
        builder.HasOne<Location>().WithMany().HasForeignKey(e => e.LocationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(nameof(TenantEntity.TenantId), nameof(Geofence.LocationId)).HasDatabaseName("ix_geofence_tenant_location");
        builder.ToTable(table =>
        {
            table.HasCheckConstraint("ck_geofence_kind", "`kind` IN (1, 2)");
            table.HasCheckConstraint(
                "ck_geofence_shape",
                "(`kind` = 1 AND `center_latitude` IS NOT NULL AND `center_longitude` IS NOT NULL AND `radius_metres` BETWEEN 1 AND "
                + $"{GeofenceShape.MaxRadiusMetres} AND `polygon_geo_json` IS NULL) OR (`kind` = 2 AND `polygon_geo_json` IS NOT NULL)");
        });
    }
}

internal sealed class GradeConfiguration : IEntityTypeConfiguration<Grade>
{
    public void Configure(EntityTypeBuilder<Grade> builder)
    {
        CodedMasterMapping.Map(builder);
        builder.ToTable(table => table.HasCheckConstraint("ck_grade_rank", $"`rank` BETWEEN 0 AND {Grade.MaxRank}"));
    }
}

internal sealed class DesignationConfiguration : IEntityTypeConfiguration<Designation>
{
    public void Configure(EntityTypeBuilder<Designation> builder)
    {
        CodedMasterMapping.Map(builder);
        builder.HasOne<Grade>().WithMany().HasForeignKey(e => e.GradeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(nameof(TenantEntity.TenantId), nameof(Designation.GradeId)).HasDatabaseName("ix_designation_tenant_grade");
    }
}

internal sealed class CostCentreConfiguration : IEntityTypeConfiguration<CostCentre>
{
    public void Configure(EntityTypeBuilder<CostCentre> builder)
    {
        CodedMasterMapping.Map(builder);
        builder.HasOne<LegalEntity>().WithMany().HasForeignKey(e => e.LegalEntityId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(nameof(TenantEntity.TenantId), nameof(CostCentre.LegalEntityId)).HasDatabaseName("ix_cost_centre_tenant_legal_entity");
    }
}

internal sealed class EmployeeTypeConfiguration : IEntityTypeConfiguration<EmployeeType>
{
    public void Configure(EntityTypeBuilder<EmployeeType> builder) => CodedMasterMapping.Map(builder);
}

internal sealed class OrgUnitHeadConfiguration : IEntityTypeConfiguration<OrgUnitHead>
{
    public void Configure(EntityTypeBuilder<OrgUnitHead> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Ignore(e => e.Period);
        builder.HasOne<OrgUnit>().WithMany().HasForeignKey(e => e.OrgUnitId).OnDelete(DeleteBehavior.Restrict);

        // One row per unit and start date: serves the head lookup on a date, and stops two racing changes.
        builder.HasIndex(nameof(TenantEntity.TenantId), nameof(OrgUnitHead.OrgUnitId), nameof(OrgUnitHead.EffectiveFrom))
            .IsUnique()
            .HasDatabaseName("ux_org_unit_head_tenant_unit_from");
    }
}
