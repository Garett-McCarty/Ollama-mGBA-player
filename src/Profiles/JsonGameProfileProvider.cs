using System.Text.Json;

namespace OllamaNetGB.Profiles;

public sealed class JsonGameProfileProvider : IGameProfileProvider
{
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNameCaseInsensitive = true
	};

	public async Task<GameProfile> ResolveAsync(
		string gameId,
		CancellationToken cancellationToken)
	{
		gameId = NormalizeGameId(gameId);
		var path = Path.Combine(AppContext.BaseDirectory, "profiles", gameId + ".json");
		if (File.Exists(path))
		{
			await using var stream = File.OpenRead(path);
			var profile = await JsonSerializer.DeserializeAsync<GameProfile>(
				stream,
				JsonOptions,
				cancellationToken);
			if (profile is not null)
				return profile;
		}

		return new GameProfile(
			gameId,
			string.IsNullOrWhiteSpace(gameId) ? "Unknown GBA game" : gameId,
			"Learn this game's controls, observe cause and effect, avoid repeated failures, and make measurable progress.",
			[]);
	}

	public IReadOnlyList<string> Search(GameProfile profile, string query, int limit = 5)
	{
		var terms = query
			.Split([' ', '\t', '\r', '\n', ',', '.', ':', ';'], StringSplitOptions.RemoveEmptyEntries)
			.Where(term => term.Length >= 3)
			.Select(term => term.ToLowerInvariant())
			.Distinct()
			.ToArray();

		return profile.Knowledge
			.Select(entry => new
			{
				Text = $"{entry.Topic}: {entry.Content}",
				Score = terms.Count(term =>
					entry.Topic.Contains(term, StringComparison.OrdinalIgnoreCase) ||
					entry.Content.Contains(term, StringComparison.OrdinalIgnoreCase))
			})
			.OrderByDescending(item => item.Score)
			.Take(Math.Clamp(limit, 1, 10))
			.Select(item => item.Text)
			.ToArray();
	}

	private static string NormalizeGameId(string gameId)
	{
		var normalized = new string(gameId
			.Trim()
			.ToLowerInvariant()
			.Select(character => char.IsLetterOrDigit(character) ? character : '-')
			.ToArray());
		normalized = string.Join('-', normalized.Split('-', StringSplitOptions.RemoveEmptyEntries));
		return normalized == "emerald" ||
			   (normalized.Contains("pokemon", StringComparison.Ordinal) &&
				normalized.Contains("emerald", StringComparison.Ordinal))
			? "pokemon-emerald"
			: normalized;
	}
}
