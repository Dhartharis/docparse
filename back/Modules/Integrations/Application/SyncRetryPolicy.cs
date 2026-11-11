namespace DocParse.Modules.Integrations.Application;

public sealed record SyncFailureAction(bool Restart, bool Retry, int DelaySeconds);
public static class SyncRetryPolicy
{
    public static SyncFailureAction Decide(int attempts,bool retryable,int? retryAfter,bool cursorExpired,bool hasCursor,int interval)
    {
        if(attempts is <1 or >5 || interval is <60 or >86400)throw new ArgumentOutOfRangeException(nameof(attempts));
        if(cursorExpired&&hasCursor)return new(true,false,interval);
        var retry=retryable&&attempts<5;
        return new(false,retry,retry?Math.Max(new[]{10,30,120,300}[attempts-1],retryAfter is >=1 and <=3600?retryAfter.Value:0):0);
    }
}
