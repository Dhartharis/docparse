using DocParse.Modules.Knowledge.Contracts;
using DocParse.Modules.Usage.Contracts;
using System.Text;

namespace DocParse.Modules.Chat.Application;

public sealed record SourceReference(Guid DocumentId, string Title, int? PageNumber = null);
public sealed record ChatAnswer(string Answer, IReadOnlyList<SourceReference> Sources,string? Code=null);

public sealed record ChatTurn(string Question, string Answer);

public interface IAnswerGenerator
{
    Task<string> GenerateAsync(string question, IReadOnlyList<KnowledgeSource> sources, IReadOnlyList<ChatTurn> history, CancellationToken cancellationToken);
}

public sealed class QueryService(IKnowledgeCatalog knowledge, IAnswerGenerator answers,IUsageMeter usage)
{
    public async Task<ChatAnswer> QueryDocumentsAsync(Guid organizationId, IReadOnlyCollection<Guid> knowledgeBaseIds, string question, IReadOnlyList<ChatTurn> history, CancellationToken cancellationToken,Guid? botId=null,string channel="web")
    {
        var recent = history.TakeLast(2).Select(turn => $"Previous question: {turn.Question}\nPrevious answer: {turn.Answer}");
        var retrievalQuery = string.Join("\n", recent.Append("Current question: " + question));
        if(knowledgeBaseIds.Count==0)return new ChatAnswer("No relevant information was found in this bot's knowledge bases.", [],"no_sources");
        var operation=await usage.StartAsync(organizationId,botId,"chat",channel,Encoding.UTF8.GetByteCount(retrievalQuery)+200,8192,768,cancellationToken);
        try {
        var sources = await knowledge.SearchAsync(organizationId, knowledgeBaseIds, retrievalQuery, cancellationToken);
        // Leave space for fresh evidence and the system prompt in the local model context.
        var remaining = Math.Max(0, 18000 - question.Length - sources.Sum(source => source.Content.Length) - 2000);
        var boundedHistory = new List<ChatTurn>();
        foreach (var turn in history.Reverse())
        {
            var length = turn.Question.Length + turn.Answer.Length;
            if (length > remaining) break;
            boundedHistory.Insert(0, turn); remaining -= length;
        }
        if (sources.Count == 0) { await operation.CompleteAsync(true,CancellationToken.None); return new ChatAnswer("No relevant information was found in this bot's knowledge bases.", [],"no_sources"); }
        var answer=await answers.GenerateAsync(question, sources, boundedHistory, cancellationToken);
        await operation.CompleteAsync(true,CancellationToken.None);
        return new ChatAnswer(answer,
            sources.Select(x => new SourceReference(x.DocumentId, x.Title, x.PageNumber)).Distinct().ToArray());
        } catch { await operation.CompleteAsync(false,CancellationToken.None); throw; }

    }
}

public sealed class AnswerGenerationUnavailable() : Exception("The local answer model is unavailable.");
