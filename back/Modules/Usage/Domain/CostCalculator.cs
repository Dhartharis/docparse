namespace DocParse.Modules.Usage.Domain;

public sealed record CostScenario(string Name, long Queries, decimal InputTokensPerQuery, decimal OutputTokensPerQuery,
    decimal QueryEmbeddingTokens, decimal DocumentEmbeddingTokens, decimal InputUsdPerMillion,
    decimal OutputUsdPerMillion, decimal EmbeddingUsdPerMillion, decimal RetrievalUsdPerQuery,
    decimal StorageUsd, decimal EgressUsd, decimal ProcessingUsd);
public sealed record CostModel(decimal MonthlyPriceUsd, decimal TargetCostFraction, decimal FreeBudgetUsd, IReadOnlyList<CostScenario> Scenarios);
public sealed record ScenarioEstimate(string Name, decimal QueryCostUsd, decimal IngestionCostUsd, decimal TotalCostUsd,
    decimal CostBudgetUsd, decimal? CostFraction, long? MaximumQueriesWithinBudget, bool WithinBudget, bool WithinAbsoluteMargin);

public static class CostCalculator
{
    public static IReadOnlyList<ScenarioEstimate> Calculate(CostModel model)
    {
        if (model.MonthlyPriceUsd < 0 || model.MonthlyPriceUsd > 1_000_000 || model.FreeBudgetUsd < 0 || model.FreeBudgetUsd > 1_000_000
            || model.TargetCostFraction <= 0 || model.TargetCostFraction > .5m || model.Scenarios is null || model.Scenarios.Count is < 1 or > 10)
            throw new ArgumentException("Invalid cost model.");
        return model.Scenarios.Select(s =>
        {
            if(s is null)throw new ArgumentException("Invalid scenario.");
            decimal[] values = [s.InputTokensPerQuery, s.OutputTokensPerQuery, s.QueryEmbeddingTokens, s.DocumentEmbeddingTokens,
                s.InputUsdPerMillion, s.OutputUsdPerMillion, s.EmbeddingUsdPerMillion, s.RetrievalUsdPerQuery, s.StorageUsd, s.EgressUsd, s.ProcessingUsd];
            if (string.IsNullOrWhiteSpace(s.Name) || s.Name.Length > 100 || s.Queries < 0 || s.Queries > 1_000_000_000
                || values.Any(x => x < 0 || x > 1_000_000_000)) throw new ArgumentException("Invalid scenario.");
            var perQuery = (s.InputTokensPerQuery * s.InputUsdPerMillion + s.OutputTokensPerQuery * s.OutputUsdPerMillion
                + s.QueryEmbeddingTokens * s.EmbeddingUsdPerMillion) / 1_000_000m + s.RetrievalUsdPerQuery;
            var ingestion = s.DocumentEmbeddingTokens * s.EmbeddingUsdPerMillion / 1_000_000m + s.ProcessingUsd;
            var fixedCost = ingestion + s.StorageUsd + s.EgressUsd;
            var total = fixedCost + perQuery * s.Queries;
            var budget = model.MonthlyPriceUsd == 0 ? model.FreeBudgetUsd : model.MonthlyPriceUsd * model.TargetCostFraction;
            var maximum = perQuery == 0 ? (long?)null : (long)Math.Min(long.MaxValue, Math.Max(0, decimal.Floor((budget - fixedCost) / perQuery)));
            return new ScenarioEstimate(s.Name, perQuery, ingestion, total, budget,
                model.MonthlyPriceUsd == 0 ? null : total / model.MonthlyPriceUsd, maximum,
                total <= budget, model.MonthlyPriceUsd == 0 ? total <= model.FreeBudgetUsd : total <= model.MonthlyPriceUsd * .5m);
        }).ToArray();
    }
}
