using System.Text.Json;
using Castr.Core.Security;

namespace Castr.Core.Trust;

/// <summary>Reads/writes the trust store JSON format shared by trusted-senders.seed.json and a receiver's persisted local store.</summary>
public static class TrustStoreJsonCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static IReadOnlyList<TrustEntry> Decode(string json)
    {
        var document = JsonSerializer.Deserialize<TrustStoreDocument>(json, Options)
            ?? throw new InvalidDataException("Trust store JSON deserialized to null.");

        return [.. document.Entries.Select(ToEntry)];
    }

    public static string Encode(IReadOnlyList<TrustEntry> entries)
    {
        var document = new TrustStoreDocument
        {
            Version = 1,
            Entries = [.. entries.Select(ToDto)],
        };
        return JsonSerializer.Serialize(document, Options);
    }

    private static TrustEntry ToEntry(TrustEntryDto dto) => new(
        PublicKeyId: new PublicKeyId(dto.PublicKeyId ?? throw new InvalidDataException("Entry missing publicKeyId.")),
        DisplayName: dto.DisplayName ?? "",
        Status: dto.Status switch
        {
            "trusted" => TrustStatus.Trusted,
            "blocked" => TrustStatus.Blocked,
            var other => throw new InvalidDataException($"Unknown trust status '{other}'."),
        },
        AddedAt: dto.AddedAt ?? DateTimeOffset.UnixEpoch,
        Source: dto.Source switch
        {
            "pre-deployed" => TrustEntrySource.PreDeployed,
            "manual" or null => TrustEntrySource.Manual,
            var other => throw new InvalidDataException($"Unknown trust source '{other}'."),
        });

    private static TrustEntryDto ToDto(TrustEntry entry) => new()
    {
        PublicKeyId = entry.PublicKeyId.Value,
        DisplayName = entry.DisplayName,
        Status = entry.Status == TrustStatus.Trusted ? "trusted" : "blocked",
        AddedAt = entry.AddedAt,
        Source = entry.Source == TrustEntrySource.PreDeployed ? "pre-deployed" : "manual",
    };

    private sealed class TrustStoreDocument
    {
        public int Version { get; set; } = 1;
        public List<TrustEntryDto> Entries { get; set; } = [];
    }

    private sealed class TrustEntryDto
    {
        public string? PublicKeyId { get; set; }
        public string? DisplayName { get; set; }
        public string? Status { get; set; }
        public DateTimeOffset? AddedAt { get; set; }
        public string? Source { get; set; }
    }
}
