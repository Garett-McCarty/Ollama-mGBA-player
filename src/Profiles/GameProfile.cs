namespace OllamaNetGB.Profiles;

public sealed record GameProfile(
    string GameId,
    string DisplayName,
    string Prompt,
    IReadOnlyList<KnowledgeEntry> Knowledge);

public sealed record KnowledgeEntry(string Topic, string Content);

public interface IGameProfileProvider
{
    Task<GameProfile> ResolveAsync(
        string gameId,
        CancellationToken cancellationToken);

    IReadOnlyList<string> Search(GameProfile profile, string query, int limit = 5);
}
