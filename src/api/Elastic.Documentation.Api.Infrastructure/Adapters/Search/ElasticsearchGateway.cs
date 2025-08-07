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

	[JsonPropertyName("url_segment_count")]
	public int UrlSegmentCount { get; init; }
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

		// Simple single query - synonyms are handled at the analyzer level
		Console.WriteLine($"Search query: '{query}'");

		// Precise title-focused search with exact phrase matching priority
		var shouldQueries = new List<Action<QueryDescriptor<DocumentDto>>>();

		// Use the query directly - synonyms are handled by Elasticsearch analyzers
		var searchQuery = query;

		if (searchQuery.Contains("esql", StringComparison.InvariantCultureIgnoreCase))
		{
			searchQuery = searchQuery.Replace("esql", "ES|QL", StringComparison.OrdinalIgnoreCase);
		}

		// Strategy: Use function_score to boost documents with fewer URL segments
		shouldQueries.Add(sh => sh.FunctionScore(fs => fs
			.Query(q => q.Bool(b => b
				.Should(
					// Highest priority: exact prefix match
					s => s.Prefix(p => p
						.Field("title.keyword")
						.Value(searchQuery)
						.CaseInsensitive(true)
						.Boost(1100.0f)
					),

					// High priority: bool prefix matching
					s => s.MatchBoolPrefix(m => m
						.Field(f => f.Title)
						.Query(searchQuery)
						.Boost(1000.0f)
					),

					// Medium priority: all terms must match
					s => s.Match(m => m
						.Field(f => f.Title)
						.Query(searchQuery)
						.Operator(Operator.And)
						.Boost(500.0f)
					),

					// Lower priority: fuzzy matching for typos/abbreviations
					s => s.Match(m => m
						.Field(f => f.Title)
						.Query(searchQuery)
						.Fuzziness(2) // Allow 2 character differences
						.Boost(50.0f)
					),

					// // IMPORTANT: Wildcard matching with high boost for partial word matches
					// s => s.Bool(wildcardBool => wildcardBool
					// 		.Should(searchQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries)
					// 			.SelectMany(term => new Action<QueryDescriptor<DocumentDto>>[]
					// 			{
					// 				// term* pattern (e.g., "bool*" to match "Boolean")
					// 				q => q.Wildcard(w => w
					// 					.Field(f => f.Title)
					// 					.Value($"{term}*")
					// 					.CaseInsensitive(true)
					// 				),
					// 				// *term* pattern (e.g., "*bool*" to match "Boolean")
					// 				q => q.Wildcard(w => w
					// 					.Field(f => f.Title)
					// 					.Value($"*{term}*")
					// 					.CaseInsensitive(true)
					// 				)
					// 			}).ToArray())
					// 		.MinimumShouldMatch(1)
					// 		.Boost(200.0f) // Higher boost for wildcard matches
					// ),

					// Lowest priority: any term match
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
				// Boost based on URL segment count - fewer segments = higher score
				f => f.FieldValueFactor(fvf => fvf
						.Field("url_segment_count")
						.Factor(10.0f) // Positive factor
						.Modifier(FieldValueFactorModifier.Reciprocal) // 10/segments
						.Missing(5) // Default for documents without the field
				)
			)
			.BoostMode(FunctionBoostMode.Multiply) // Multiply the scores
			.ScoreMode(FunctionScoreMode.Multiply)
		));

		Console.WriteLine($"Added {shouldQueries.Count} query clauses");

		var response = await _client.SearchAsync<DocumentDto>(s => s
			.Indices(_elasticsearchOptions.IndexName)
			.Query(q => q
				.Bool(b => b
					.Must(
						// Main search query
						m => m.Bool(bb => bb
							.Should(shouldQueries.ToArray())
							.MinimumShouldMatch(1)
						)
					)
					.MustNot(
						// Exclude 404 page
						mn => mn.Term(t => t
							.Field("url.keyword")
							.Value("/docs/404")
						)
					)
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
			foreach (var hit in response.Hits.Take(5))
			{
				var doc = hit.Source;
				var urlSegments = doc?.Url?.Split('/', StringSplitOptions.RemoveEmptyEntries).Length ?? 0;
				var urlFactor = urlSegments > 0 ? 10.0f / urlSegments : 10.0f / 5;
				Console.WriteLine(
					$"  Score: {hit.Score:F2} | Title: '{doc?.Title}' | URL: '{doc?.Url}' | URL segments: {urlSegments} | URL factor: {urlFactor:F2}");

				// Special debug for 404 page
				if (doc?.Title?.Contains("not found", StringComparison.OrdinalIgnoreCase) == true || doc?.Url?.Contains("404") == true)
				{
					Console.WriteLine($"    *** 404 PAGE DETECTED *** URL: '{doc.Url}' | Title: '{doc.Title}'");
				}

				// Special debug for Java vs APM Java agent comparison
				if (query.Contains("java", StringComparison.OrdinalIgnoreCase))
				{
					var titleLower = doc?.Title?.ToLowerInvariant();
					var hasJava = titleLower?.Contains("java") == true;
					var hasAgent = titleLower?.Contains("agent") == true;
					var startsWithJava = titleLower?.StartsWith("java", StringComparison.InvariantCulture) == true;
					Console.WriteLine($"    Java debug: hasJava={hasJava}, hasAgent={hasAgent}, startsWithJava={startsWithJava}");
				}
			}
		}

		// Also try a simple test query to see what's happening
		Console.WriteLine("\n=== DEBUGGING: Testing individual queries ===");
		await DebugIndividualQueries(query, ctx);

		// Special debug for "bool query" vs "Boolean query" matching
		if (query.Contains("bool", StringComparison.OrdinalIgnoreCase))
		{
			Console.WriteLine("\n=== DEBUGGING: Bool vs Boolean matching ===");
			await DebugBooleanMatching(query, ctx);
		}

		// Test 404 exclusion specifically
		Console.WriteLine("\n=== DEBUGGING: Testing 404 exclusion ===");
		await TestNotFoundExclusion(ctx);

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
				("match_and", q => q.Match(m => m.Field(f => f.Title).Query(query).Operator(Operator.And))),
				("prefix_keyword", (Action<QueryDescriptor<DocumentDto>>)(q => q.Prefix(p => p.Field("title.keyword").Value(query).CaseInsensitive(true))))
			};

			foreach (var (name, queryBuilder) in testQueries)
			{
				var response = await _client.SearchAsync<DocumentDto>(s => s
					.Indices(_elasticsearchOptions.IndexName)
					.Query(queryBuilder)
					.Size(5), ctx);

				Console.WriteLine($"\n{name.ToUpperInvariant()} results for '{query}':");
				if (response.Hits != null)
				{
					foreach (var hit in response.Hits.Take(5))
					{
						var doc = hit.Source;
						var urlSegments = doc?.Url?.Split('/', StringSplitOptions.RemoveEmptyEntries).Length ?? 0;
						Console.WriteLine($"  Score: {hit.Score:F2} | Title: '{doc?.Title}' | URL segments: {urlSegments}");
					}
				}
			}

			// Special test for java agent terms individually
			if (query.Contains("java", StringComparison.OrdinalIgnoreCase) && query.Contains("agent", StringComparison.OrdinalIgnoreCase))
			{
				Console.WriteLine($"\n=== TESTING INDIVIDUAL TERMS ===");

				var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
				foreach (var term in terms)
				{
					var termResponse = await _client.SearchAsync<DocumentDto>(s => s
						.Indices(_elasticsearchOptions.IndexName)
						.Query(q => q.Match(m => m.Field(f => f.Title).Query(term)))
						.Size(3), ctx);

					Console.WriteLine($"\nTerm '{term}' results:");
					if (termResponse.Hits != null)
					{
						foreach (var hit in termResponse.Hits.Take(3))
						{
							var doc = hit.Source;
							var urlSegments = doc?.Url?.Split('/', StringSplitOptions.RemoveEmptyEntries).Length ?? 0;
							Console.WriteLine($"  Score: {hit.Score:F2} | Title: '{doc?.Title}' | URL segments: {urlSegments}");
						}
					}
				}
			}
		}
		catch (Exception ex)
		{
			Console.WriteLine($"Debug query failed: {ex.Message}");
		}
	}

	private async Task DebugBooleanMatching(string query, Cancel ctx)
	{
		try
		{
			Console.WriteLine($"Testing why '{query}' doesn't match 'Boolean query'...");

			// Test if "Boolean query" exists at all
			var exactResponse = await _client.SearchAsync<DocumentDto>(s => s
				.Indices(_elasticsearchOptions.IndexName)
				.Query(q => q.Term(t => t.Field("title.keyword").Value("Boolean query")))
				.Size(1), ctx);

			Console.WriteLine($"Exact 'Boolean query' search found: {exactResponse.Total} documents");

			// Test our current queries against "Boolean query"
			var testQueries = new (string, Action<QueryDescriptor<DocumentDto>>)[]
			{
				("match_bool_prefix", q => q.MatchBoolPrefix(m => m.Field(f => f.Title).Query(query))),
				("match_and", q => q.Match(m => m.Field(f => f.Title).Query(query).Operator(Operator.And))),
				("prefix_keyword", q => q.Prefix(p => p.Field("title.keyword").Value(query).CaseInsensitive(true)))
			};

			foreach (var (name, queryBuilder) in testQueries)
			{
				var response = await _client.SearchAsync<DocumentDto>(s => s
					.Indices(_elasticsearchOptions.IndexName)
					.Query(queryBuilder)
					.Size(10), ctx);

				Console.WriteLine($"\n{name.ToUpperInvariant()} results for '{query}':");
				var booleanResults = response.Hits?.Where(h => h.Source?.Title?.Contains("Boolean", StringComparison.OrdinalIgnoreCase) == true).ToList();
				Console.WriteLine($"  Found {booleanResults?.Count ?? 0} Boolean-related results out of {response.Hits?.Count ?? 0} total");

				if (booleanResults?.Count > 0)
				{
					foreach (var hit in booleanResults.Take(3))
					{
						Console.WriteLine($"    Score: {hit.Score:F2} | Title: '{hit.Source?.Title}'");
					}
				}
			}
		}
		catch (Exception ex)
		{
			Console.WriteLine($"Boolean debug failed: {ex.Message}");
		}
	}

	private async Task TestNotFoundExclusion(Cancel ctx)
	{
		try
		{
			// Test if we can find the 404 page at all
			var find404Response = await _client.SearchAsync<DocumentDto>(s => s
				.Indices(_elasticsearchOptions.IndexName)
				.Query(q => q.Term(t => t
					.Field(f => f.Url)
					.Value("/docs/404")
				))
				.Size(1), ctx);

			Console.WriteLine($"Direct 404 search found: {find404Response.Total} documents");

			// Test with keyword field
			var find404KeywordResponse = await _client.SearchAsync<DocumentDto>(s => s
				.Indices(_elasticsearchOptions.IndexName)
				.Query(q => q.Term(t => t
					.Field("url.keyword")
					.Value("/docs/404")
				))
				.Size(1), ctx);

			Console.WriteLine($"Direct 404 search (keyword) found: {find404KeywordResponse.Total} documents");

			// Test exclusion query
			var excludeResponse = await _client.SearchAsync<DocumentDto>(s => s
				.Indices(_elasticsearchOptions.IndexName)
				.Query(q => q.Bool(b => b
					.MustNot(mn => mn.Term(t => t
						.Field("url.keyword")
						.Value("/docs/404")
					))
				))
				.Size(5), ctx);

			Console.WriteLine($"Exclusion query returned: {excludeResponse.Total} documents");
			var has404 = excludeResponse.Hits?.Any(h => h.Source?.Url?.Contains("404") == true) == true;
			Console.WriteLine($"Exclusion results contain 404 page: {has404}");
		}
		catch (Exception ex)
		{
			Console.WriteLine($"404 exclusion test failed: {ex.Message}");
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
			Score = (float)(response.Hits?.ElementAtOrDefault(index)?.Score ?? 0.0)
		}).ToList();

		return (totalHits, results);
	}
}

[JsonSerializable(typeof(DocumentDto))]
internal sealed partial class EsJsonContext : JsonSerializerContext;
