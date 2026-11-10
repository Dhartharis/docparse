using System.Net.Mail;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;

namespace DocParse.Modules.Access;

internal static class OnboardingEndpoints {
 public static void MapOwnerOnboarding(this WebApplication app){
  app.MapPost("/api/onboarding/register",async(RegisterOwner input,AccessDbContext db,UserManager<ApplicationUser> users,LocalEmailSender mail,IConfiguration config,CancellationToken ct)=>{
   var email=input.Email?.Trim()??"";var name=input.OrganizationName?.Trim()??"";
   if(string.IsNullOrWhiteSpace(input.Password)||name.Length is <1 or >200||email.Length>254||!MailAddress.TryCreate(email,out var address)||address.Address!=email||string.IsNullOrWhiteSpace(input.UserName)||input.UserName.Length>64||input.UserName.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c is not('-' or '_' or '.')))return Results.BadRequest();
   if(await users.FindByEmailAsync(email)is not null)return Results.Conflict(new{code="account_exists"});
   await using var tx=await db.Database.BeginTransactionAsync(ct);var user=new ApplicationUser{Id=Guid.NewGuid(),Email=email,UserName=input.UserName,Locale=EmailTemplates.Locale(input.Locale)};
   var created=await users.CreateAsync(user,input.Password);if(!created.Succeeded)return Results.ValidationProblem(created.Errors.GroupBy(e=>e.Code).ToDictionary(g=>g.Key,g=>g.Select(e=>e.Description).ToArray()));
   var organization=new Organization{Name=name};db.Organizations.Add(organization);db.Memberships.Add(new Membership{OrganizationId=organization.Id,UserId=user.Id,Role="Admin"});
   var code=WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(await users.GenerateEmailConfirmationTokenAsync(user)));
   var path=QueryHelpers.AddQueryString("/api/auth/confirmEmail",new Dictionary<string,string?>{{"userId",user.Id.ToString()},{"code",code}});
   var link=new Uri(new Uri(config["PublicApi:BaseUrl"]??"http://127.0.0.1:5128"),path).AbsoluteUri;
   db.MailOutbox.Add(mail.PrepareConfirmation(user,email,link));await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return Results.Accepted();
  }).RequireRateLimiting("authentication");
 }
 private sealed record RegisterOwner(string Email,string UserName,string Password,string OrganizationName,string Locale="en");
}
