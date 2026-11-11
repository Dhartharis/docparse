using System.Text;
using System.Text.RegularExpressions;
using DocParse.Modules.Integrations.Contracts;

namespace DocParse.Modules.Integrations.Application;

public sealed class LiveQueryFailure(string code, int? retryAfterSeconds = null) : Exception("The source query could not be completed.")
{
    public string Code { get; } = code;
    public int? RetryAfterSeconds { get; } = retryAfterSeconds;
}
public static class LiveQueryRules
{
    public static void Validate(LiveQuery query)
    {
        if (query.Fields is not { Length: >= 1 and <= 3 } || query.Fields.Distinct().Count() != query.Fields.Length ||
            query.Fields.Any(value => value is not ("catalog" or "price" or "availability")) ||
            query.Values is not { Length: >= 1 and <= 50 } || query.Values.Distinct(StringComparer.Ordinal).Count() != query.Values.Length ||
            query.SelectorKind is not ("externalIds" or "skus" or "text") || query.SelectorKind == "text" && query.Values.Length != 1)
            throw new LiveQueryFailure("validation");
        foreach (var value in query.Values)
            if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) || value.EnumerateRunes().Count() > (query.SelectorKind == "text" ? 256 : 128) ||
                query.SelectorKind == "externalIds" && !Regex.IsMatch(value, @"\A[A-Za-z0-9][A-Za-z0-9._:-]{0,127}\z"))
                throw new LiveQueryFailure("validation");
    }
}
