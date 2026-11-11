using System.Net;
using System.Net.Mail;
using Microsoft.AspNetCore.Identity;

namespace DocParse.Modules.Access;

internal sealed class LocalEmailSender(IConfiguration configuration,IHostEnvironment environment,MailOutboxStore outbox):IEmailSender<ApplicationUser>
{
 public MailOutboxMessage PrepareConfirmation(ApplicationUser user,string email,string link){var content=EmailTemplates.Render("confirmation",user.Locale,new Dictionary<string,string>{{"link",BrowserLink(link)}});return outbox.Prepare(email,content.Subject,content.Body);}
 public Task SendConfirmationLinkAsync(ApplicationUser user,string email,string link){var content=EmailTemplates.Render("confirmation",user.Locale,new Dictionary<string,string>{{"link",BrowserLink(link)}});return outbox.EnqueueAsync(email,content.Subject,content.Body);}
 public Task SendPasswordResetLinkAsync(ApplicationUser user,string email,string resetLink){var content=EmailTemplates.Render("reset_link",user.Locale,new Dictionary<string,string>{{"link",BrowserLink(resetLink)}});return outbox.EnqueueAsync(email,content.Subject,content.Body);}
 public Task SendPasswordResetCodeAsync(ApplicationUser user,string email,string resetCode){var content=EmailTemplates.Render("reset",user.Locale,new Dictionary<string,string>{{"code",resetCode}});return outbox.EnqueueAsync(email,content.Subject,content.Body);}
 public MailOutboxMessage PrepareInvitation(Invitation invitation,string email,string token,string organization){var site=configuration["PublicWeb:BaseUrl"]??"http://127.0.0.1:5173";var link=new Uri(new Uri(site),"/#invite="+token).AbsoluteUri;var content=EmailTemplates.Render("invitation",invitation.Locale,new Dictionary<string,string>{{"link",link},{"token",token},{"organization",organization}});return outbox.Prepare(email,content.Subject,content.Body,invitation.Id,invitation.Generation);}
 private string BrowserLink(string generated){var original=new Uri(WebUtility.HtmlDecode(generated));var api=new Uri(configuration["PublicApi:BaseUrl"]??"http://127.0.0.1:5128");return new Uri(api,original.PathAndQuery).AbsoluteUri;}
 public async Task SendRawAsync(StoredMail mail,CancellationToken ct){if(!environment.IsDevelopment())throw new InvalidOperationException("Configure a production email transport.");using var client=new SmtpClient(configuration["Email:Host"]??"127.0.0.1",1025);using var message=new MailMessage("no-reply@docparse.local",mail.Recipient,mail.Subject,mail.Body){IsBodyHtml=true};using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(10));await client.SendMailAsync(message,timeout.Token);}
}
