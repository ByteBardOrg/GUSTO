namespace ByteBard.GUSTO;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

internal sealed record JobPayload(object[] Arguments, string? TraceParent, string? TraceState);

internal static class JobPayloadSerializer
{
    internal const int CurrentVersion = 1;

    internal static string Serialize(object?[] arguments, string? traceParent, string? traceState, JsonSerializerSettings settings)
    {
        var serializer = JsonSerializer.Create(settings);
        var serializedArguments = new JArray();
        foreach (var argument in arguments)
        {
            serializedArguments.Add(argument == null
                ? JValue.CreateNull()
                : JToken.FromObject(argument, serializer));
        }

        var envelope = new JObject
        {
            ["version"] = CurrentVersion,
            ["arguments"] = serializedArguments
        };

        if (!string.IsNullOrEmpty(traceParent))
        {
            envelope["traceparent"] = traceParent;
        }

        if (!string.IsNullOrEmpty(traceState))
        {
            envelope["tracestate"] = traceState;
        }

        return envelope.ToString(Formatting.None);
    }

    internal static JobPayload Deserialize(string json, JsonSerializerSettings settings)
    {
        var token = JToken.Parse(json);
        var serializer = JsonSerializer.Create(settings);

        if (token.Type == JTokenType.Array)
        {
            return new JobPayload(token.ToObject<object[]>(serializer) ?? Array.Empty<object>(), null, null);
        }

        if (token is not JObject envelope)
        {
            throw new JsonSerializationException("The job payload must be a legacy argument array or a GUSTO envelope object.");
        }

        // TypeNameHandling.All represented legacy object[] values as a $type/$values object.
        if (envelope["version"] == null && envelope["$values"] != null)
        {
            return new JobPayload(envelope.ToObject<object[]>(serializer) ?? Array.Empty<object>(), null, null);
        }

        var versionToken = envelope["version"]
            ?? throw new JsonSerializationException("The GUSTO job payload envelope is missing 'version'.");
        var version = versionToken.ToObject<int>();
        if (version != CurrentVersion)
        {
            throw new NotSupportedException(
                $"Unsupported GUSTO job payload version '{version}'. Supported version is {CurrentVersion}.");
        }

        var argumentsToken = envelope["arguments"]
            ?? throw new JsonSerializationException("The GUSTO job payload envelope is missing 'arguments'.");

        return new JobPayload(
            argumentsToken.ToObject<object[]>(serializer) ?? Array.Empty<object>(),
            envelope.Value<string>("traceparent"),
            envelope.Value<string>("tracestate"));
    }
}
