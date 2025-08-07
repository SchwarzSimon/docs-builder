// Licensed to Elasticsearch B.V under one or more agreements.
// Elasticsearch B.V licenses this file to you under the Apache 2.0 License.
// See the LICENSE file in the project root for more information

using System.Text.Json.Serialization;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.QueryDsl;
using Elastic.Clients.Elasticsearch.Serialization;
using Elastic.Documentation.Api.Core.Search;
using Elastic.Transport;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Elastic.Documentation.Api.Infrastructure.Adapters.Search;

internal sealed record DocumentDto
{
	[JsonPropertyName("title")]
	public required string Title { get; init; }

	[JsonPropertyName("url")]
	public required string Url { get; init; }

	[JsonPropertyName("description")]
	public string? Description { get; init; }

	[JsonPropertyName("body")]
	public string? Body { get; init; }

	[JsonPropertyName("abstract")]
	public required string Abstract { get; init; }

	[JsonPropertyName("url_segment_count")]
	public int UrlSegmentCount { get; init; }

	[JsonPropertyName("parents")]
	public ParentDocumentDto[] Parents { get; init; } = [];
}

internal sealed record ParentDocumentDto
{
	[JsonPropertyName("title")]
	public required string Title { get; init; }

	[JsonPropertyName("url")]
	public required string Url { get; init; }
}

public class ElasticsearchGateway : ISearchGateway
{
	private readonly ElasticsearchClient _client;
	private readonly ElasticsearchOptions _elasticsearchOptions;
	private readonly ILogger<ElasticsearchGateway> _logger;

	public ElasticsearchGateway(ElasticsearchOptions elasticsearchOptions, ILogger<ElasticsearchGateway> logger)
	{
		_logger = logger;
		_elasticsearchOptions = elasticsearchOptions;
		var nodePool = new SingleNodePool(new Uri(elasticsearchOptions.Url.Trim()));
		var clientSettings = new ElasticsearchClientSettings(
				nodePool,
				sourceSerializer: (_, settings) => new DefaultSourceSerializer(settings, EsJsonContext.Default))
			.DefaultIndex(elasticsearchOptions.IndexName)
			.Authentication(new ApiKey(elasticsearchOptions.ApiKey));

		_client = new ElasticsearchClient(clientSettings);
	}

	public async Task<(int TotalHits, List<SearchResultItem> Results)> SearchAsync(string query, int pageNumber, int pageSize, Cancel ctx = default) =>
		await ExactSearchAsync(query, pageNumber, pageSize, ctx);

	public async Task<(int TotalHits, List<SearchResultItem> Results)> ExactSearchAsync(string query, int pageNumber, int pageSize, Cancel ctx = default)
	{
		_logger.LogInformation("Starting search for '{Query}' with pageNumber={PageNumber}, pageSize={PageSize}", query, pageNumber, pageSize);
		_logger.LogDebug("Elasticsearch URL: {Url}, Index Name: {IndexName}", _elasticsearchOptions.Url, _elasticsearchOptions.IndexName);
		_logger.LogDebug("Building query strategy for '{Query}'", query);
		var shouldQueries = new List<Action<QueryDescriptor<DocumentDto>>>();
		var searchQuery = query;

		// Strategy: Use function_score to boost documents with fewer URL segments
		// Each clause below is a different way to match the user's query, with different priorities (boosts).
		shouldQueries.Add(sh => sh.FunctionScore(fs => fs
			.Query(q => q.Bool(b => b
				.Should(
					// Highest priority: exact prefix match on the title (case-insensitive).
					// This matches documents whose title starts with the query string.
					s => s.Prefix(p => p
						.Field("title.keyword")
						.Value(searchQuery)
						.CaseInsensitive(true)
						.Boost(300.0f)
					),

					// High priority: match_bool_prefix allows for partial word matches at the end of the query.
					// Useful for autocomplete-like scenarios.
					s => s.MatchBoolPrefix(m => m
						.Field(f => f.Title)
						.Query(searchQuery)
						.Boost(250.0f)
					),

					// Medium priority: all terms in the query must appear in the title.
					// This ensures strong relevance when all words are present.
					s => s.Match(m => m
						.Field(f => f.Title)
						.Query(searchQuery)
						.Operator(Operator.And)
						.Boost(200.0f)
					),

					// True semantic search on Abstract (higher boost than before)
					// This leverages semantic capabilities for more relevant results.
					s => s.Semantic(sem => sem
						.Field("abstract")
						.Query(searchQuery)
						.Boost(200.0f)
					),

					// Semantic search on semantic_text field
					s => s.Match(m => m
						.Field("semantic_text")
						.Query(searchQuery)
						.Boost(100.0f)
					),

					// Fallback: match on Abstract for non-semantic indices
					s => s.Match(m => m
						.Field(f => f.Abstract)
						.Query(searchQuery)
						.Boost(75.0f)
					),

					// Lower priority: fuzzy matching for typos/abbreviations
					// Fuzziness(2) allows up to two character differences.
					s => s.Match(m => m
						.Field(f => f.Title)
						.Query(searchQuery)
						.Fuzziness(2)
						.Boost(50.0f)
					),

					// Lowest priority: any term in the query can match in the title.
					// This is a broad match for recall.
					s => s.Match(m => m
						.Field(f => f.Title)
						.Query(searchQuery)
						.Operator(Operator.Or)
						.Boost(1.0f)
					)
				)
				.MinimumShouldMatch(1)
			))
			.Functions(
				// Boost based on URL segment count: documents with fewer segments (closer to the root)
				// are considered more important and get a higher score.
				f => f.FieldValueFactor(fvf => fvf
						.Field("url_segment_count")
						.Factor(10.0f) // Positive factor
						.Modifier(FieldValueFactorModifier.Reciprocal) // Score = 10 / segments
						.Missing(5) // Default value if the field is missing
				)
			)
			.BoostMode(FunctionBoostMode.Multiply) // Multiply the function score with the query score
			.ScoreMode(FunctionScoreMode.Multiply)
		));

		_logger.LogDebug("Added {ShouldQueriesCount} query clauses", shouldQueries.Count);

		try
		{
			var response = await _client.SearchAsync<DocumentDto>(s => s
				.Indices(_elasticsearchOptions.IndexName)
				.Query(q => q
					.Bool(b => b
						.Must(
							// Main search query: combines all the above strategies.
							m => m.Bool(bb => bb
								.Should(shouldQueries.ToArray())
								.MinimumShouldMatch(1)
							)
						)
						.MustNot(
							// Exclude the 404 page from results.
							mn => mn.Term(t => t
								.Field("url.keyword")
								.Value("/docs/404")
							),
							// Exclude the docs root page from results.
							mn => mn.Term(t => t
								.Field("url.keyword")
								.Value("/docs")
							)
						)
					)
				)
				.Rescore(rescore => rescore
					.WindowSize(50)
					.Query(rq => rq
							// Rescore top 50 results using semantic search on abstract.
							// This can improve ranking for the most relevant results.
							.Query(q => q.Semantic(m => m
								.Field("abstract")
								.Query(searchQuery)
							))
							.QueryWeight(0.3f)
							.RescoreQueryWeight(1f)
					)
				)
				.Sort(sort => sort
					.Field(f => f.Field("_score").Order(SortOrder.Desc))
				)
				.From((pageNumber - 1) * pageSize)
				.Size(pageSize), ctx);

			if (!response.IsValidResponse)
			{
				_logger.LogWarning("Elasticsearch search response was not valid. Reason: {Reason}", response.ElasticsearchServerError?.Error?.Reason ?? "Unknown");
			}
			else
			{
				_logger.LogInformation("Search completed for '{Query}'. Total hits: {TotalHits}", query, response.Total);
			}

			return ProcessSearchResponse(response);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Error occurred during Elasticsearch search for '{Query}'", query);
			throw;
		}
	}


	private static (int TotalHits, List<SearchResultItem> Results) ProcessSearchResponse(SearchResponse<DocumentDto> response)
	{
		var totalHits = (int)response.Total;

		var results = response.Documents.Select((doc, index) => new SearchResultItem
		{
			Url = doc.Url,
			Title = doc.Title,
			Description = doc.Description ?? string.Empty,
			Parents = doc.Parents.Select(parent => new SearchResultItemParent
			{
				Title = parent.Title,
				Url = parent.Url
			}).ToArray(),
			Score = (float)(response.Hits.ElementAtOrDefault(index)?.Score ?? 0.0)
		}).ToList();

		return (totalHits, results);
	}
}

[JsonSerializable(typeof(DocumentDto))]
[JsonSerializable(typeof(ParentDocumentDto))]
internal sealed partial class EsJsonContext : JsonSerializerContext;
