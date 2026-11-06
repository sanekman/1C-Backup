using System.Text.Json;

namespace SPBackup.Server;

internal static class ConfigurationCompatibility
{
    internal static IReadOnlyList<string> ReadHeader(HttpContext context)
    {
        try { return AgentCapabilities.Parse(context.Request.Headers[AgentCapabilities.Header].ToString()); }
        catch (ArgumentException) { throw new ServerRequestException(400, "invalid_agent_capabilities"); }
    }

    internal static void RequireSupport(string document, IReadOnlyList<string> supported)
    {
        try
        {
            if (System.Text.Encoding.UTF8.GetByteCount(document) > 4 * 1024 * 1024) throw new JsonException();
            using var json = JsonDocument.Parse(document);
            var root = json.RootElement;
            if (!root.TryGetProperty("protocolVersion", out var protocol) || !protocol.TryGetInt32(out var version)
                || version != 1 || !root.TryGetProperty("jobs", out var jobs)
                || jobs.ValueKind != JsonValueKind.Array || jobs.GetArrayLength() > 10000) throw new JsonException();
            if (jobs.GetArrayLength() == 0) return; // Existing registration/ETags remain usable by legacy agents.
            var required = new HashSet<string>(StringComparer.Ordinal)
                { AgentCapabilities.ConfigurationV1, AgentCapabilities.StrictConfigurationV1, AgentCapabilities.ManagedExecutionV1 };
            foreach (var job in jobs.EnumerateArray())
            {
                if (job.ValueKind != JsonValueKind.Object) throw new JsonException();
                // Inspect every spelling, not just the first matching property. Ambiguous JSON
                // must not hide a TLS requirement from compatibility negotiation.
                foreach (var field in job.EnumerateObject().Where(p => p.Name.Equals("postgreSql", StringComparison.OrdinalIgnoreCase)))
                {
                    if (field.Value.ValueKind == JsonValueKind.Null) continue;
                    if (field.Value.ValueKind != JsonValueKind.Object) throw new JsonException();
                    foreach (var tls in field.Value.EnumerateObject().Where(p => p.Name.Equals("tls", StringComparison.OrdinalIgnoreCase)))
                    {
                        if (tls.Value.ValueKind == JsonValueKind.Null) continue;
                        if (tls.Value.ValueKind != JsonValueKind.Object) throw new JsonException();
                        required.Add(AgentCapabilities.PostgreSqlTlsV1);
                    }
                }
            }
            if (!required.IsSubsetOf(supported)) throw new ServerRequestException(409, "agent_capabilities_insufficient");
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        { throw new ServerRequestException(409, "configuration_format_unsupported"); }
    }
}
