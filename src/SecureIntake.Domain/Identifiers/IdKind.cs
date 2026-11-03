namespace SecureIntake.Domain.Identifiers;

/// <summary>Typed external identifier kinds (foundations F5). The prefix is validated on input.</summary>
public enum IdKind
{
    Organisation,
    Dropbox,
    Grant,
    UploadSession,
    Submission,
    File,
    Key,
    User,
    Receipt,
    Job,
    Elevation,
    Approval,
    ServiceAccount,
    Invitation,
    Session,
    AuditRecord,
    Webhook,
}

public static class IdKinds
{
    public static string Prefix(this IdKind kind) => kind switch
    {
        IdKind.Organisation => "org",
        IdKind.Dropbox => "dbx",
        IdKind.Grant => "grt",
        IdKind.UploadSession => "upl",
        IdKind.Submission => "sub",
        IdKind.File => "file",
        IdKind.Key => "key",
        IdKind.User => "usr",
        IdKind.Receipt => "rcpt",
        IdKind.Job => "job",
        IdKind.Elevation => "elev",
        IdKind.Approval => "appr",
        IdKind.ServiceAccount => "svc",
        IdKind.Invitation => "inv",
        IdKind.Session => "ses",
        IdKind.AuditRecord => "aud",
        IdKind.Webhook => "whk",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
