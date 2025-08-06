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
}

public class ElasticsearchGateway : ISearchGateway
{
	private readonly ElasticsearchClient _client;
	private readonly ElasticsearchOptions _elasticsearchOptions;

	public ElasticsearchGateway(ElasticsearchOptions elasticsearchOptions)
	{
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

	/// <summary>
	/// Performs exact case-insensitive search for precise matching
	/// </summary>
	public async Task<(int TotalHits, List<SearchResultItem> Results)> ExactSearchAsync(string query, int pageNumber, int pageSize, Cancel ctx = default)
	{
		Console.WriteLine($"Searching for '{query}' with pageNumber={pageNumber}, pageSize={pageSize}");
		Console.WriteLine($"Elasticsearch URL: {_elasticsearchOptions.Url}");
		Console.WriteLine($"Index Name: {_elasticsearchOptions.IndexName}");

		// Debug: Log the individual scoring strategy
		Console.WriteLine($"Query strategy for '{query}':");

		// Handle specific query transformations
		var searchQueries = GetSearchQueries(query);
		Console.WriteLine($"Search queries: {string.Join(", ", searchQueries.Select(q => $"'{q}'"))}");

		// Precise title-focused search with exact phrase matching priority
		var shouldQueries = new List<Action<QueryDescriptor<DocumentDto>>>();

		foreach (var searchQuery in searchQueries)
		{
			// Simplified strategy: Only use the best-performing query from debug
			shouldQueries.Add(sh => sh.Bool(b => b
				.Should(
					// Use bool prefix since it gave equal scores (1.00) to all "Elasticsearch" titles
					s => s.MatchBoolPrefix(m => m
						.Field(f => f.Title)
						.Query(searchQuery)
						.Boost(1000.0f)
					),

					// Fallback: standard text matching with much lower boost
					s => s.Match(m => m
						.Field(f => f.Title)
						.Query(searchQuery)
						.Operator(Operator.And)
						.Boost(1.0f)
					)
				)
				.MinimumShouldMatch(1)
			));
		}

		Console.WriteLine($"Added {shouldQueries.Count} query clauses");

		var response = await _client.SearchAsync<DocumentDto>(s => s
			.Indices(_elasticsearchOptions.IndexName)
			.Query(q => q
				.Bool(b => b
					.Should(shouldQueries.ToArray())
					.MinimumShouldMatch(1)
				)
			)
			.Sort(sort => sort
				.Field(f => f.Field("_score").Order(SortOrder.Desc))
			)
			.From((pageNumber - 1) * pageSize)
			.Size(pageSize), ctx);

		Console.WriteLine($"Elasticsearch response status: {response.ApiCallDetails?.HttpStatusCode}");
		Console.WriteLine($"Total hits: {response.Total}");
		Console.WriteLine($"Documents returned: {response.Documents?.Count ?? 0}");

		// Debug: Show top 5 results with their scores and explanations
		if (response.Hits != null)
		{
			Console.WriteLine("Top results:");
			foreach (var hit in response.Hits.Take(3))
			{
				var doc = hit.Source;
				Console.WriteLine($"  Score: {hit.Score:F2} | Title: '{doc?.Title}'");
			}
		}

		// Also try a simple test query to see what's happening
		Console.WriteLine("\n=== DEBUGGING: Testing individual queries ===");
		await DebugIndividualQueries(query, ctx);

		if (response.ApiCallDetails?.OriginalException != null)
		{
			Console.WriteLine($"Elasticsearch error: {response.ApiCallDetails.OriginalException.Message}");
		}

		return ProcessSearchResponse(response);
	}

	private async Task DebugIndividualQueries(string query, Cancel ctx)
	{
		try
		{
			// Test individual query types to see which is causing high scores
			var testQueries = new[]
			{
				("match_phrase", q => q.MatchPhrase(mp => mp.Field(f => f.Title).Query(query))),
				("match_phrase_prefix", q => q.MatchPhrasePrefix(mpp => mpp.Field(f => f.Title).Query(query).MaxExpansions(10))),
				("match_bool_prefix", q => q.MatchBoolPrefix(m => m.Field(f => f.Title).Query(query))),
				("match_and", (Action<QueryDescriptor<DocumentDto>>)(q => q.Match(m => m.Field(f => f.Title).Query(query).Operator(Operator.And))))
			};

			foreach (var (name, queryBuilder) in testQueries)
			{
				var response = await _client.SearchAsync<DocumentDto>(s => s
					.Indices(_elasticsearchOptions.IndexName)
					.Query(queryBuilder)
					.Size(3), ctx);

				Console.WriteLine($"\n{name.ToUpperInvariant()} results:");
				if (response.Hits != null)
				{
					foreach (var hit in response.Hits.Take(3))
					{
						Console.WriteLine($"  Score: {hit.Score:F2} | Title: '{hit.Source?.Title}'");
					}
				}
			}
		}
		catch (Exception ex)
		{
			Console.WriteLine($"Debug query failed: {ex.Message}");
		}
	}

	private static List<string> GetSearchQueries(string query)
	{
		var queries = new List<string>
		{
			query
		}; // Always include the original query

		// Handle specific transformations
		var lowerQuery = query.ToLowerInvariant().Trim();

		switch (lowerQuery)
		{
			case "esql":
				queries.Add("ES|QL");
				break;
			case "dotnet":
				queries.Add(".NET");
				break;
		}

		return queries;
	}

	private static (int TotalHits, List<SearchResultItem> Results) ProcessSearchResponse(SearchResponse<DocumentDto> response)
	{
		var totalHits = (int)response.Total;

		var results = response.Documents.Select((doc, index) => new SearchResultItem
		{
			Url = doc.Url,
			Title = doc.Title,
			Description = doc.Description ?? string.Empty,
			Score = (float)(response.Hits?.ElementAtOrDefault(index)?.Score ?? 0.0)
		}).ToList();

		return (totalHits, results);
	}
}

[JsonSerializable(typeof(DocumentDto))]
internal sealed partial class EsJsonContext : JsonSerializerContext;
