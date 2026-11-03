using System.Text.Json;
using System.Text.Json.Serialization;
using SecureIntake.Contracts.Operations;
using SecureIntake.Contracts.Organisations;

namespace SecureIntake.Api;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CreateOrganisationRequest))]
[JsonSerializable(typeof(OrganisationResponse))]
[JsonSerializable(typeof(OrganisationListResponse))]
[JsonSerializable(typeof(SuspendOrganisationRequest))]
[JsonSerializable(typeof(CreateDropboxRequest))]
[JsonSerializable(typeof(DropboxResponse))]
[JsonSerializable(typeof(DropboxListResponse))]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(HealthStatusResponse))]
[JsonSerializable(typeof(ProblemResponse))]
public sealed partial class ApiJsonContext : JsonSerializerContext;
