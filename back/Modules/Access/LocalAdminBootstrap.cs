using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace DocParse.Modules.Access;

// Explicit CLI bootstrap, never an HTTP endpoint or automatic startup seed.
internal static class LocalAdminBootstrap
{
    public static async Task RunAsync(IServiceProvider services, IHostEnvironment environment, IConfiguration configuration)
    {
        if (!environment.IsDevelopment()) throw new InvalidOperationException("Local bootstrap is available only in Development.");
        var email = configuration["LocalAdmin:Email"] ?? throw new InvalidOperationException("Set LocalAdmin__Email.");
        var username = configuration["LocalAdmin:UserName"] ?? throw new InvalidOperationException("Set LocalAdmin__UserName.");
        var password = configuration["LocalAdmin:Password"] ?? throw new InvalidOperationException("Set LocalAdmin__Password.");
        if (!System.Net.Mail.MailAddress.TryCreate(email, out var address) || address.Address != email)
            throw new InvalidOperationException("Use a valid local account email.");
        using var scope = services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var db = scope.ServiceProvider.GetRequiredService<AccessDbContext>();
        if (await users.FindByNameAsync(username) is not null || await users.FindByEmailAsync(email) is not null)
            throw new InvalidOperationException("Account already exists; bootstrap will not overwrite credentials.");
        await using var transaction = await db.Database.BeginTransactionAsync();
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = username, Email = email, EmailConfirmed = true };
        // The explicitly requested local fixture may use a weak password without relaxing registration policy.
        user.PasswordHash = users.PasswordHasher.HashPassword(user, password);
        var result = await users.CreateAsync(user);
        if (!result.Succeeded) throw new InvalidOperationException("Local account validation failed.");
        var organization = new Organization { Name = "DocParse Local" };
        db.Organizations.Add(organization);
        db.Memberships.Add(new Membership { OrganizationId = organization.Id, UserId = user.Id, Role = "Admin" });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        Console.WriteLine("Local Admin created. Credentials were not printed.");
    }
}
