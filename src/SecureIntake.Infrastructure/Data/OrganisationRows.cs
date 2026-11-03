using System.Data.Common;
using SecureIntake.Domain.Intake;
using SecureIntake.Domain.Organisations;

namespace SecureIntake.Infrastructure.Data;

internal static class Rows
{
    public const string OrganisationColumns = "id, display_name, slug, method, status, version, created_at";

    public static Organisation ReadOrganisation(DbDataReader r, SqlDialect d) => new(
        d.ReadId(r, 0), r.GetString(1), r.GetString(2),
        Enum.Parse<ProvisioningMethod>(r.GetString(3)), Enum.Parse<OrganisationStatus>(r.GetString(4)),
        r.GetInt64(5), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(6)));

    public const string DropboxColumns = "id, organisation_id, name, status, version";

    public static Dropbox ReadDropbox(DbDataReader r, SqlDialect d) => new(
        d.ReadId(r, 0), d.ReadId(r, 1), r.GetString(2), Enum.Parse<DropboxStatus>(r.GetString(3)), r.GetInt64(4));
}
