using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ToadFlow.Server.Data.Entities;
using ToadFlow.Server.Modules;

namespace ToadFlow.Server.Data;

public sealed class ToadFlowDb(DbContextOptions<ToadFlowDb> options, IEnumerable<IServerModule> modules) : DbContext(options)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public DbSet<User> Users => Set<User>();
    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();
    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<RegistrationCode> RegistrationCodes => Set<RegistrationCode>();
    public DbSet<Source> Sources => Set<Source>();
    public DbSet<Schedule> Schedules => Set<Schedule>();
    public DbSet<ScheduleChange> ScheduleChanges => Set<ScheduleChange>();
    public DbSet<Run> Runs => Set<Run>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<JobLogLine> JobLogLines => Set<JobLogLine>();
    public DbSet<SystemEvent> SystemEvents => Set<SystemEvent>();
    public DbSet<Setting> Settings => Set<Setting>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configuration)
    {
        // Перечисления — строками: базу можно читать глазами, а SQL очереди не зависит от порядка значений.
        configuration.Properties<Enum>().HaveConversion<string>().HaveMaxLength(32);
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<User>(user => user.HasIndex(u => u.Login).IsUnique());

        model.Entity<ApiToken>(token =>
        {
            token.HasIndex(t => t.Hash).IsUnique();
            token.HasOne<User>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<Agent>(agent =>
        {
            agent.Property(a => a.Revits).AsJson();
            agent.Property(a => a.Limits).AsJson();
        });

        model.Entity<RegistrationCode>().HasKey(c => c.Code);

        model.Entity<Schedule>(schedule =>
        {
            schedule.Property(s => s.Selection).AsJson();
            schedule.Property(s => s.ModuleSettingsJson).HasColumnType("jsonb");
            schedule.Property(s => s.LastKnownModels).AsJson();
            schedule.HasOne<Source>().WithMany().HasForeignKey(s => s.SourceId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<ScheduleChange>(change =>
        {
            change.HasIndex(c => new { c.ScheduleId, c.At });
            change.HasOne<Schedule>().WithMany().HasForeignKey(c => c.ScheduleId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<Run>(run =>
        {
            run.HasIndex(r => r.CreatedAt);
            run.HasOne<Schedule>().WithMany().HasForeignKey(r => r.ScheduleId).OnDelete(DeleteBehavior.SetNull);
        });

        model.Entity<Job>(job =>
        {
            job.Property(j => j.PayloadJson).HasColumnType("jsonb");
            job.Property(j => j.ResultJson).HasColumnType("jsonb");
            job.HasIndex(j => new { j.Status, j.RevitYear, j.Priority, j.CreatedAt });
            job.HasIndex(j => j.RunId);
            job.HasOne<Run>().WithMany().HasForeignKey(j => j.RunId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<JobLogLine>(line =>
        {
            line.HasIndex(l => new { l.JobId, l.Id });
            line.HasOne<Job>().WithMany().HasForeignKey(l => l.JobId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<SystemEvent>(e => e.HasIndex(x => x.At).IsDescending());

        model.Entity<Setting>().HasKey(s => s.Key);

        foreach (var module in modules) module.ConfigureModel(model);
    }
}

public static class JsonColumn
{
    /// <summary>Хранит значение как jsonb через System.Text.Json; сравнение — по сериализованному виду.</summary>
    public static PropertyBuilder<T> AsJson<T>(this PropertyBuilder<T> property)
    {
        property.HasColumnType("jsonb").HasConversion(
            value => JsonSerializer.Serialize(value, ToadFlowDb.Json),
            json => JsonSerializer.Deserialize<T>(json, ToadFlowDb.Json)!,
            new ValueComparer<T>(
                (a, b) => JsonSerializer.Serialize(a, ToadFlowDb.Json) == JsonSerializer.Serialize(b, ToadFlowDb.Json),
                value => JsonSerializer.Serialize(value, ToadFlowDb.Json).GetHashCode(),
                value => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, ToadFlowDb.Json), ToadFlowDb.Json)!));
        return property;
    }
}
