using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace DocParse.Modules.Access;

public sealed class ApplicationUser : IdentityUser<Guid> { public string Locale { get; set; } = "en"; }

public sealed class Organization
{
    public Guid? CreatedByUserId { get; set; }
    public Guid? CreationRequestId { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class Membership
{
    public Guid OrganizationId { get; set; }
    public Guid UserId { get; set; }
    public string Role { get; set; } = "Viewer";
}

// Provider subjects are mapped to the stable DocParse user ID; email is never an identity-link key.
public sealed class IdentityLink
{
    public string Provider { get; set; } = "";
    public string Issuer { get; set; } = "";
    public string Subject { get; set; } = "";
    public Guid UserId { get; set; }
}

public sealed class Invitation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public string Locale { get; set; } = "en";
    public Guid Generation { get; set; } = Guid.NewGuid();
    public Guid? AcceptedUserId { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? TokenCiphertext { get; set; }
    public DateTime? LastSentAt { get; set; }
    public string Source { get; set; } = "team";
    public string Email { get; set; } = "";
    public string Role { get; set; } = "Viewer";
    public string TokenHash { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
    public DateTime? AcceptedAt { get; set; }
}

public sealed class AccessDbContext(DbContextOptions<AccessDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Membership> Memberships => Set<Membership>();
    public DbSet<IdentityLink> IdentityLinks => Set<IdentityLink>();
    public DbSet<MailOutboxMessage> MailOutbox => Set<MailOutboxMessage>();
    public DbSet<Invitation> Invitations => Set<Invitation>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema("access");
        builder.Entity<Organization>(entity =>
        {
            entity.ToTable("organizations");
            entity.Property(x => x.Name).HasMaxLength(200);
        });
        builder.Entity<Membership>(entity =>
        {
            entity.ToTable("memberships", table => table.HasCheckConstraint("membership_role", "\"Role\" IN ('Admin', 'Viewer')"));
            entity.HasKey(x => new { x.OrganizationId, x.UserId });
            entity.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId);
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId);
        });
        builder.Entity<IdentityLink>(entity =>
        {
            entity.ToTable("identity_links");
            entity.HasKey(x => new { x.Provider, x.Issuer, x.Subject });
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId);
        });
        builder.Entity<MailOutboxMessage>(entity=>{entity.ToTable("mail_outbox");entity.HasKey(m=>m.Id);entity.HasOne<Invitation>().WithMany().HasForeignKey(m=>m.InvitationId);});
        builder.Entity<Invitation>(entity =>
        {
            entity.ToTable("invitations", table => table.HasCheckConstraint("invitation_role", "\"Role\" IN ('Admin', 'Viewer')"));
            entity.HasIndex(x => x.TokenHash).IsUnique();
            entity.HasIndex(x => new { x.OrganizationId, x.Email }).IsUnique();
            entity.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId);
        });
    }
}

public sealed class MailOutboxMessage {
 public Guid Id { get; set; }=Guid.NewGuid();public Guid? InvitationId { get; set; }public Guid? Generation { get; set; }
 public string Payload { get; set; }="";public string State { get; set; }="pending";public int Attempts { get; set; }
 public DateTime NextAttemptAt { get; set; }=DateTime.UtcNow;public Guid? LeaseId { get; set; }public DateTime? LeaseUntil { get; set; }
 public DateTime CreatedAt { get; set; }=DateTime.UtcNow;public DateTime UpdatedAt { get; set; }=DateTime.UtcNow;
}
