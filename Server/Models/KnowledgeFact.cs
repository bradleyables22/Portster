namespace Portster;

// Source is an attribution, not authorization. Confirmations cannot be written via tools.
public sealed record KnowledgeFact(string Name, string Value, KnowledgeSource Source,
    string? Reference = null, bool Confirmed = false, DateTimeOffset? ValidatedUtc = null);
