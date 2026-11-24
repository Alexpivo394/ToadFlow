using Microsoft.EntityFrameworkCore;

namespace ToadFlow.Server.Usage.Data;

internal static class UsageModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<UsageMachine>(machine =>
        {
            machine.ToTable("usage_machines");
            machine.HasKey(m => m.Key);
            machine.Property(m => m.Key).HasMaxLength(100);
            machine.Property(m => m.Name).HasMaxLength(100);
            machine.Property(m => m.DiskKind).HasMaxLength(16);
            machine.Property(m => m.RevitYears).HasDefaultValueSql("'{}'::integer[]");
            machine.HasIndex(m => m.LastSeenAt);
        });

        model.Entity<UsageSession>(session =>
        {
            session.ToTable("usage_sessions");
            session.Property(s => s.Id).ValueGeneratedNever();
            session.Property(s => s.MachineKey).HasMaxLength(100);
            session.Property(s => s.End).HasMaxLength(16);
            session.HasIndex(s => s.StartedAt);
            session.HasIndex(s => new { s.UserId, s.StartedAt });
            session.HasIndex(s => new { s.MachineKey, s.StartedAt });
        });

        model.Entity<UsageSessionDay>(day =>
        {
            day.ToTable("usage_session_days");
            day.HasKey(d => new { d.SessionId, d.Day });
            day.Property(d => d.MachineKey).HasMaxLength(100);
            day.HasIndex(d => d.Day);
        });

        model.Entity<UsageOperation>(operation =>
        {
            operation.ToTable("usage_operations");
            operation.Property(o => o.Id).ValueGeneratedNever();
            operation.Property(o => o.MachineKey).HasMaxLength(100);
            operation.Property(o => o.Kind).HasMaxLength(16);
            operation.Property(o => o.Outcome).HasMaxLength(16);
            operation.Property(o => o.Storage).HasMaxLength(16);
            operation.HasIndex(o => o.StartedAt);
            operation.HasIndex(o => new { o.ModelKey, o.StartedAt });
            operation.HasIndex(o => new { o.MachineKey, o.StartedAt });
            operation.HasIndex(o => new { o.UserId, o.StartedAt });
        });

        model.Entity<UsageCommandDay>(command =>
        {
            command.ToTable("usage_command_days");
            command.HasKey(c => new { c.SessionId, c.Day, c.Source, c.CommandId });
            command.Property(c => c.Source).HasMaxLength(16);
            command.Property(c => c.CommandId).HasMaxLength(200);
            command.Property(c => c.MachineKey).HasMaxLength(100);
            command.HasIndex(c => c.Day);
            command.HasIndex(c => new { c.UserId, c.Day });
        });

        model.Entity<UsageWebDay>(web =>
        {
            web.ToTable("usage_web_days");
            web.HasKey(w => new { w.UserId, w.Day, w.Space, w.Page });
            web.Property(w => w.Space).HasMaxLength(100);
            web.Property(w => w.Page).HasMaxLength(100);
            web.HasIndex(w => w.Day);
        });

        model.Entity<UsagePluginDay>(plugin =>
        {
            plugin.ToTable("usage_plugin_days");
            plugin.HasKey(p => new { p.UserId, p.Day, p.Area });
            plugin.Property(p => p.Area).HasMaxLength(50);
            plugin.HasIndex(p => p.Day);
        });
    }
}
