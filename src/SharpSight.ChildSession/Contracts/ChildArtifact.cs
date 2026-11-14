using System.Text.Json;

namespace SharpSight.ChildSession.Contracts;

public sealed record ChildArtifact(string RelativePath, string MediaType, long Length, JsonElement Metadata);
