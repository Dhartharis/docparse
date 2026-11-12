namespace DocParse.Modules.Integrations.Infrastructure;

public sealed class GoogleDriveOptions
{
    public const string Scope = "https://www.googleapis.com/auth/drive.file";
    public bool Enabled { get; init; }
    public string ClientId { get; init; } = "";
    public string ClientSecret { get; init; } = "";
    public string PickerApiKey { get; init; } = "";
    public string ProjectNumber { get; init; } = "";
    public string RedirectUri { get; init; } = "";
    public string AuthorizationEndpoint { get; init; } = "https://accounts.google.com/o/oauth2/v2/auth";
    public string TokenEndpoint { get; init; } = "https://oauth2.googleapis.com/token";
    public string DriveEndpoint { get; init; } = "https://www.googleapis.com/drive/v3/";
    public string SheetsEndpoint { get; init; } = "https://sheets.googleapis.com/v4/";
    public void Validate(bool development, string webUrl)
    {
        if (!Enabled) return;
        if (new[] { ClientId, ClientSecret, PickerApiKey, ProjectNumber }.Any(string.IsNullOrWhiteSpace) ||
            !System.Text.RegularExpressions.Regex.IsMatch(ProjectNumber, @"\A[0-9]{1,30}\z") ||
            !Uri.TryCreate(webUrl, UriKind.Absolute, out var web) || !Uri.TryCreate(RedirectUri, UriKind.Absolute, out var redirect) ||
            redirect.GetLeftPart(UriPartial.Authority) != web.GetLeftPart(UriPartial.Authority) || redirect.AbsolutePath != "/google-drive-callback.html" ||
            redirect.Query.Length != 0 || redirect.Fragment.Length != 0 || redirect.UserInfo.Length != 0 ||
            (redirect.Scheme != "https" && !(development && redirect.Scheme == "http" && redirect.IsLoopback)))
            throw new InvalidOperationException("Google Drive needs credentials and a same-origin HTTPS callback (Development loopback allowed).");
        var endpoints = new[] { (AuthorizationEndpoint, "https://accounts.google.com/o/oauth2/v2/auth"), (TokenEndpoint, "https://oauth2.googleapis.com/token"),
            (DriveEndpoint, "https://www.googleapis.com/drive/v3/"), (SheetsEndpoint, "https://sheets.googleapis.com/v4/") };
        foreach (var (value, expected) in endpoints)
            if (value != expected && !(development && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "http" &&
                (uri.IsLoopback || uri.Host == "host.docker.internal") && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0))
                throw new InvalidOperationException("Google endpoints are fixed outside local Development fixtures.");
    }
}
