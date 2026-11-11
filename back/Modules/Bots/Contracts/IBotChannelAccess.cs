namespace DocParse.Modules.Bots.Contracts;

public sealed record PublicBot(Guid Id, string Name, string WelcomeMessage, string AccentColor, Guid? LogoVersion);
public sealed record ChannelBot(ChatBot Bot, PublicBot Branding);
public interface IBotChannelAccess
{
    Task<ChannelBot?> AuthenticateKeyAsync(Guid organizationId, Guid botId, string credential, CancellationToken ct);
    Task<ChannelBot?> AuthenticateEmbedAsync(Guid botId, string credential, CancellationToken ct);
}
