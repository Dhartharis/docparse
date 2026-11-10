using Microsoft.EntityFrameworkCore;

namespace DocParse.Modules.Bots;

public sealed class Bot
{
    public DateTime? DeletedAt { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public string Name { get; set; } = "";
    public string WelcomeMessage { get; set; } = "";
    public string AccentColor { get; set; } = "#7441ec";
    public Guid? LogoVersion { get; set; }
}

public sealed class BotKnowledgeBase
{
    public Guid OrganizationId { get; set; }
    public Guid BotId { get; set; }
    public Guid KnowledgeBaseId { get; set; }
}

public sealed class BotsDbContext(DbContextOptions<BotsDbContext> options) : DbContext(options)
{
    public DbSet<Bot> Bots => Set<Bot>();
    public DbSet<BotKnowledgeBase> KnowledgeBases => Set<BotKnowledgeBase>();
    public DbSet<BotViewer> Viewers => Set<BotViewer>();
    public DbSet<PendingBotAccess> PendingAccess => Set<PendingBotAccess>();
    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.HasDefaultSchema("bots");
        builder.Entity<PendingBotAccess>(entity =>
        {
            entity.ToTable("pending_access");
            entity.Property(x=>x.InvitationId).HasColumnName("invitation_id");
            entity.Property(x=>x.InvitationGeneration).HasColumnName("invitation_generation");
            entity.HasKey(x => new { x.OrganizationId, x.BotId, x.Email });
            entity.Property(x => x.OrganizationId).HasColumnName("organization_id");
            entity.Property(x => x.BotId).HasColumnName("bot_id");
            entity.Property(x => x.Email).HasColumnName("email").HasMaxLength(254);
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
        });
        builder.Entity<Bot>(entity =>
        {
            entity.ToTable("bots");
            entity.Property(x=>x.DeletedAt).HasColumnName("deleted_at");
            entity.HasQueryFilter(x=>x.DeletedAt==null);
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.OrganizationId).HasColumnName("organization_id");
            entity.Property(x => x.Name).HasColumnName("name").HasMaxLength(200);
            entity.Property(x => x.WelcomeMessage).HasColumnName("welcome_message").HasMaxLength(2000);
            entity.Property(x => x.AccentColor).HasColumnName("accent_color").HasMaxLength(7);
            entity.Property(x => x.LogoVersion).HasColumnName("logo_version");
        });
        builder.Entity<BotKnowledgeBase>(entity =>
        {
            entity.ToTable("bot_knowledge_bases");
            entity.HasKey(x => new { x.OrganizationId, x.BotId, x.KnowledgeBaseId });
            entity.Property(x => x.OrganizationId).HasColumnName("organization_id");
            entity.Property(x => x.BotId).HasColumnName("bot_id");
            entity.Property(x => x.KnowledgeBaseId).HasColumnName("knowledge_base_id");
        });
        builder.Entity<BotViewer>(entity =>
        {
            entity.ToTable("bot_viewers");
            entity.HasKey(x => new { x.OrganizationId, x.BotId, x.UserId });
            entity.Property(x => x.OrganizationId).HasColumnName("organization_id");
            entity.Property(x => x.BotId).HasColumnName("bot_id");
            entity.Property(x => x.UserId).HasColumnName("user_id");
        });
    }
}

public sealed class BotViewer
{
    public Guid OrganizationId { get; set; }
    public Guid BotId { get; set; }
    public Guid UserId { get; set; }
}

public sealed class PendingBotAccess
{
    public Guid? InvitationId { get; set; }
    public Guid? InvitationGeneration { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid BotId { get; set; }
    public string Email { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
