using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aware.Application;
using Aware.Domain;

namespace Aware.Infrastructure;

/// <summary>
/// Serialization for the local spatial model. IDs stay opaque strings and vectors
/// stay compact arrays so an exported file is readable by a human inspecting what
/// the app stored about their home (07-DATA-PRIVACY: "inspect evidence", "export model").
///
/// <para>Everything goes through a source-generated context. Reflection-based
/// <see cref="JsonSerializer"/> raises IL2026 and breaks once the WebAssembly
/// build trims: the model would round-trip in Debug and fail in a published
/// app, which is the worst place to find out.</para>
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    Converters = [
        typeof(Vector3Converter),
        typeof(RoomIdConverter),
        typeof(SpatialObjectIdConverter),
        typeof(ProjectIdConverter)])]
[JsonSerializable(typeof(SpatialRoom))]
[JsonSerializable(typeof(PrivacySettings))]
internal partial class SpatialJsonContext : JsonSerializerContext;

/// <summary>Indented variant, used only for the human-readable model export.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    WriteIndented = true,
    Converters = [
        typeof(Vector3Converter),
        typeof(RoomIdConverter),
        typeof(SpatialObjectIdConverter),
        typeof(ProjectIdConverter)])]
[JsonSerializable(typeof(SpatialRoom))]
internal partial class SpatialJsonExportContext : JsonSerializerContext;

internal sealed class Vector3Converter : JsonConverter<Vector3>
{
    public override Vector3 Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("Expected [x, y, z] for a vector.");

        Span<float> values = stackalloc float[3];
        for (var i = 0; i < 3; i++)
        {
            reader.Read();
            values[i] = reader.GetSingle();
        }
        reader.Read(); // EndArray

        return new Vector3(values[0], values[1], values[2]);
    }

    public override void Write(Utf8JsonWriter writer, Vector3 value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(value.X);
        writer.WriteNumberValue(value.Y);
        writer.WriteNumberValue(value.Z);
        writer.WriteEndArray();
    }
}

internal sealed class RoomIdConverter : JsonConverter<RoomId>
{
    public override RoomId Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) =>
        new(r.GetString() ?? string.Empty);
    public override void Write(Utf8JsonWriter w, RoomId v, JsonSerializerOptions o) =>
        w.WriteStringValue(v.Value);
}

internal sealed class SpatialObjectIdConverter : JsonConverter<SpatialObjectId>
{
    public override SpatialObjectId Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) =>
        new(r.GetString() ?? string.Empty);
    public override void Write(Utf8JsonWriter w, SpatialObjectId v, JsonSerializerOptions o) =>
        w.WriteStringValue(v.Value);
}

internal sealed class ProjectIdConverter : JsonConverter<ProjectId>
{
    public override ProjectId Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) =>
        new(r.GetString() ?? string.Empty);
    public override void Write(Utf8JsonWriter w, ProjectId v, JsonSerializerOptions o) =>
        w.WriteStringValue(v.Value);
}
