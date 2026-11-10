using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;

namespace DocParse.Modules.Access;

internal sealed record StoredMail(string Recipient,string Subject,string Body);
internal sealed class MailOutboxStore(IConfiguration config,IDataProtectionProvider protection){
 private readonly IDataProtector payloads=protection.CreateProtector("DocParse.MailOutbox.v1");
 private readonly IDataProtector tokens=protection.CreateProtector("DocParse.InvitationToken.v1");
 public string ProtectToken(string token)=>tokens.Protect(token);
 public string ReadToken(string token)=>tokens.Unprotect(token);
 public MailOutboxMessage Prepare(string recipient,string subject,string body,Guid? invitation=null,Guid? generation=null)=>new(){InvitationId=invitation,Generation=generation,Payload=payloads.Protect(JsonSerializer.Serialize(new StoredMail(recipient,subject,body)))};
 public StoredMail Read(MailOutboxMessage message)=>JsonSerializer.Deserialize<StoredMail>(payloads.Unprotect(message.Payload))!;
 public async Task EnqueueAsync(string recipient,string subject,string body){var mail=Prepare(recipient,subject,body);await using var db=new NpgsqlConnection(config["PostgreSQL:ConnectionString"]);await db.OpenAsync();await using var cmd=new NpgsqlCommand("INSERT INTO access.mail_outbox(\"Id\",\"Payload\") VALUES(@id,@payload)",db);cmd.Parameters.AddWithValue("id",mail.Id);cmd.Parameters.AddWithValue("payload",mail.Payload);await cmd.ExecuteNonQueryAsync();}
}
