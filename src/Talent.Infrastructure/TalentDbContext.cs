namespace Talent.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Talent.Domain.Constants;
using Talent.Domain.Entities;
using Talent.Domain.Enums;
using Talent.Domain.ValueObjects;

/// <summary>
/// EF Core context for the recruitment domain.
/// <para>
/// This type is why <c>Talent.Domain</c> must stay clean: the mapping lives here, so the entities it
/// maps carry no persistence attributes and no <c>DbContext</c> awareness. An earlier revision of the
/// plan put EF Core in the domain, and <c>Talent.Architecture.Tests</c> exists to stop that coming
/// back.
/// </para>
/// <para>
/// A1 adds pgvector to this same schema, so the table and column names are part of a contract with
/// another repository — renaming one is a breaking change, not a refactor.
/// </para>
/// </summary>
public sealed class TalentDbContext : DbContext
{
    /// <summary>Creates the context.</summary>
    /// <param name="options">Provider and connection options.</param>
    public TalentDbContext(DbContextOptions<TalentDbContext> options)
        : base(options)
    {
    }

    /// <summary>Job postings.</summary>
    public DbSet<Job> Jobs => this.Set<Job>();

    /// <summary>Candidate profiles.</summary>
    public DbSet<Candidate> Candidates => this.Set<Candidate>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Job>(job =>
        {
            job.ToTable("jobs");
            job.HasKey(j => j.Id);

            job.Property(j => j.Id).HasColumnName("id").ValueGeneratedNever();
            job.Property(j => j.Title).HasColumnName("title").HasMaxLength(JobSchema.MaxTitleLength).IsRequired();
            job.Property(j => j.Description).HasColumnName("description").HasMaxLength(JobSchema.MaxDescriptionLength);

            // Stored as a Postgres text[] rather than a join table: the ids are an ordered value list
            // owned by the posting, never queried independently, and A1 reads them the same way.
            job.Property(j => j.RequiredSkillIds).HasColumnName("required_skill_ids").IsRequired();

            job.Property(j => j.Seniority).HasColumnName("seniority").HasConversion<string>().IsRequired();
            job.Property(j => j.Arrangement).HasColumnName("arrangement").HasConversion<string>().IsRequired();

            // Owned types keep Location and SalaryRange as columns on `jobs`. They are value objects
            // with no identity of their own, so a separate table would be structure without meaning.
            job.OwnsOne(j => j.Location, location =>
            {
                location.Property(l => l.City).HasColumnName("city").HasMaxLength(120);
                location.Property(l => l.CountryCode).HasColumnName("country_code").HasMaxLength(Location.CountryCodeLength);
            });

            job.OwnsOne(j => j.Salary, salary =>
            {
                salary.Property(s => s.Minimum).HasColumnName("salary_min");
                salary.Property(s => s.Maximum).HasColumnName("salary_max");
                salary.Property(s => s.CurrencyCode).HasColumnName("salary_currency").HasMaxLength(3);
            });

            // search_jobs filters on country and arrangement and orders by title for a stable page
            // boundary — which is what makes a signed pagination handle mean the same thing twice.
            job.HasIndex(j => j.Title).HasDatabaseName("ix_jobs_title");
            job.HasIndex(j => j.Arrangement).HasDatabaseName("ix_jobs_arrangement");
        });

        modelBuilder.Entity<Candidate>(candidate =>
        {
            candidate.ToTable("candidates");
            candidate.HasKey(c => c.Id);

            candidate.Property(c => c.Id).HasColumnName("id").ValueGeneratedNever();
            candidate.Property(c => c.FullName).HasColumnName("full_name").HasMaxLength(CandidateSchema.MaxFullNameLength).IsRequired();
            candidate.Property(c => c.SkillIds).HasColumnName("skill_ids").IsRequired();
            candidate.Property(c => c.YearsOfExperience).HasColumnName("years_of_experience").IsRequired();
            candidate.Property(c => c.Seniority).HasColumnName("seniority").HasConversion<string>().IsRequired();
            candidate.Property(c => c.WillingToRelocate).HasColumnName("willing_to_relocate").IsRequired();

            candidate.OwnsOne(c => c.Location, location =>
            {
                location.Property(l => l.City).HasColumnName("city").HasMaxLength(120);
                location.Property(l => l.CountryCode).HasColumnName("country_code").HasMaxLength(Location.CountryCodeLength);
            });

            candidate.HasIndex(c => c.Seniority).HasDatabaseName("ix_candidates_seniority");
        });
    }
}
