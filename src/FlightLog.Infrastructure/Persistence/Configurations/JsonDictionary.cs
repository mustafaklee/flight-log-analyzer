using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace FlightLog.Infrastructure.Persistence.Configurations;

/// <summary>Stores a string dictionary as a jsonb object.</summary>
internal static class JsonDictionary
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General);

    public static PropertyBuilder<IReadOnlyDictionary<string, string>?> AsJsonb(this PropertyBuilder<IReadOnlyDictionary<string, string>?> property) =>
        property.HasColumnType("jsonb").HasConversion(
            new ValueConverter<IReadOnlyDictionary<string, string>?, string?>(
                v => v == null ? null : JsonSerializer.Serialize(v, Options),
                v => v == null ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(v, Options)),
            new ValueComparer<IReadOnlyDictionary<string, string>?>(
                (a, b) => (a == null && b == null) || (a != null && b != null && a.Count == b.Count && !a.Except(b).Any()),
                v => v == null ? 0 : v.Count,
                v => v == null ? null : new Dictionary<string, string>(v, StringComparer.Ordinal)));

    public static PropertyBuilder<IReadOnlyDictionary<string, string>> AsRequiredJsonb(this PropertyBuilder<IReadOnlyDictionary<string, string>> property) =>
        property.HasColumnType("jsonb").IsRequired().HasConversion(
            new ValueConverter<IReadOnlyDictionary<string, string>, string>(
                v => JsonSerializer.Serialize(v, Options),
                v => JsonSerializer.Deserialize<Dictionary<string, string>>(v, Options) ?? new Dictionary<string, string>()),
            new ValueComparer<IReadOnlyDictionary<string, string>>(
                (a, b) => a!.Count == b!.Count && !a.Except(b).Any(),
                v => v.Count,
                v => new Dictionary<string, string>(v, StringComparer.Ordinal)));
}

/// <summary>Stores a value as jsonb through System.Text.Json; for small, immutable values (lists, maps).</summary>
internal static class JsonValue
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General);

    public static PropertyBuilder<TValue> AsJsonb<TValue, TStored>(this PropertyBuilder<TValue> property)
        where TStored : TValue
        where TValue : class =>
        property.HasColumnType("jsonb").IsRequired().HasConversion(
            new ValueConverter<TValue, string>(
                v => JsonSerializer.Serialize(v, Options),
                v => JsonSerializer.Deserialize<TStored>(v, Options)!),
            new ValueComparer<TValue>(
                (a, b) => JsonSerializer.Serialize(a, Options) == JsonSerializer.Serialize(b, Options),
                v => JsonSerializer.Serialize(v, Options).GetHashCode(StringComparison.Ordinal),
                v => JsonSerializer.Deserialize<TStored>(JsonSerializer.Serialize(v, Options), Options)!));
}
