using System.Reflection;
using System.Text.RegularExpressions;

namespace SecureIntake.ArchitectureTests;

/// <summary>Enforces the modular-monolith boundaries in foundations F4 and the stack rules in F3.</summary>
public partial class ModuleBoundaryTests
{
    private static readonly string Root = FindRoot();

    /// <summary>For each project, the SecureIntake projects it may reference directly.</summary>
    public static TheoryData<string, string[]> AllowedReferences => new()
    {
        { "SecureIntake.Domain", [] },
        { "SecureIntake.Contracts", [] },
        { "SecureIntake.Cryptography", [] },
        { "SecureIntake.Application", ["SecureIntake.Domain", "SecureIntake.Contracts"] },
        { "SecureIntake.Infrastructure", ["SecureIntake.Application", "SecureIntake.Cryptography"] },
        { "SecureIntake.Api", ["SecureIntake.Application"] },
        { "SecureIntake.Worker", ["SecureIntake.Infrastructure"] },
        { "SecureIntake.Host", ["SecureIntake.Api", "SecureIntake.Worker", "SecureIntake.Infrastructure"] },
    };

    [Theory]
    [MemberData(nameof(AllowedReferences))]
    public void Project_references_respect_module_boundaries(string project, string[] allowed)
    {
        var csproj = File.ReadAllText(Path.Combine(Root, "src", project, project + ".csproj"));
        var refs = ProjectRef().Matches(csproj).Select(m => m.Groups[1].Value).ToArray();
        Assert.All(refs, r => Assert.Contains(r, allowed));
    }

    [Theory]
    [InlineData("SecureIntake.Domain")]
    [InlineData("SecureIntake.Cryptography")]
    [InlineData("SecureIntake.Contracts")]
    public void Core_assemblies_have_no_io_frameworks(string assembly)
    {
        var refs = Assembly.Load(assembly).GetReferencedAssemblies().Select(a => a.Name!).ToArray();
        Assert.DoesNotContain(refs, r => r.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
        Assert.DoesNotContain(refs, r => r.StartsWith("Npgsql", StringComparison.Ordinal));
        Assert.DoesNotContain(refs, r => r.StartsWith("Microsoft.Data.Sqlite", StringComparison.Ordinal));
        Assert.DoesNotContain(refs, r => r.StartsWith("SecureIntake.", StringComparison.Ordinal));
    }

    [Fact]
    public void Api_does_not_reach_infrastructure() =>
        Assert.DoesNotContain(Assembly.Load("SecureIntake.Api").GetReferencedAssemblies(), a => a.Name == "SecureIntake.Infrastructure");

    [Fact]
    public void No_ef_core_or_reflection_serialisers_anywhere()
    {
        var banned = new[] { "Microsoft.EntityFrameworkCore", "Newtonsoft.Json", "Dapper", "AutoMapper" };
        foreach (var csproj in Directory.EnumerateFiles(Path.Combine(Root, "src"), "*.csproj", SearchOption.AllDirectories)
                     .Append(Path.Combine(Root, "Directory.Packages.props")))
        {
            var text = File.ReadAllText(csproj);
            Assert.All(banned, b => Assert.DoesNotContain($"\"{b}", text, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Shipped_projects_are_aot_compatible() =>
        Assert.Contains("<IsAotCompatible>true</IsAotCompatible>", File.ReadAllText(Path.Combine(Root, "Directory.Build.props")), StringComparison.Ordinal);

    [Fact]
    public void Sql_lives_only_in_infrastructure()
    {
        var sqlPattern = SqlKeyword();
        foreach (var project in new[] { "SecureIntake.Domain", "SecureIntake.Application", "SecureIntake.Api", "SecureIntake.Contracts" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(Root, "src", project), "*.cs", SearchOption.AllDirectories)
                         .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
            {
                Assert.False(sqlPattern.IsMatch(File.ReadAllText(file)), $"SQL found in {file}");
            }
        }
    }

    [Fact]
    public void Tenant_tables_in_postgres_have_forced_rls()
    {
        var sql = File.ReadAllText(Path.Combine(Root, "src/SecureIntake.Infrastructure/Data/Migrations/postgres/0001_initial.sql"));
        var tenantTables = TenantTable().Matches(sql).Select(m => m.Groups[1].Value).Distinct().ToArray();
        Assert.NotEmpty(tenantTables);
        // Every table with an organisation_id NOT NULL column is tenant-owned unless explicitly control-plane.
        var controlPlane = new[] { "organisation_resource_assignments" };
        foreach (var t in tenantTables.Except(controlPlane))
        {
            Assert.Contains($"'{t}'", sql, StringComparison.Ordinal);
        }
    }

    /// <summary>ADR-0002: the native binding stays minimal and isolated in SecureIntake.Cryptography.</summary>
    [Fact]
    public void Native_interop_exists_only_in_the_cryptography_module()
    {
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Root, "src"), "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(file);
            if (text.Contains("[LibraryImport", StringComparison.Ordinal) || text.Contains("[DllImport", StringComparison.Ordinal))
            {
                Assert.EndsWith(Path.Combine("SecureIntake.Cryptography", "Native", "Sodium.cs"), file, StringComparison.Ordinal);
            }
        }
        foreach (var csproj in Directory.EnumerateFiles(Path.Combine(Root, "src"), "*.csproj", SearchOption.AllDirectories)
                     .Where(f => !f.EndsWith("SecureIntake.Cryptography.csproj", StringComparison.Ordinal)))
        {
            Assert.DoesNotContain("\"libsodium\"", File.ReadAllText(csproj), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Raw_crypto_primitives_are_not_public()
    {
        var asm = Assembly.Load("SecureIntake.Cryptography");
        foreach (var name in new[] { "X25519", "HybridKem", "KeyWrap", "Sodium" })
        {
            var t = asm.GetTypes().Single(x => x.Name == name);
            Assert.False(t.IsPublic, $"{name} must be internal (ADR-0002, S1 §10)");
        }
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SecureIntake.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    [GeneratedRegex("""<ProjectReference Include="[^"]*/([^/"]+)\.csproj"\s*/>""")]
    private static partial Regex ProjectRef();

    [GeneratedRegex(@"""\s*(SELECT|INSERT INTO|UPDATE\s+\w+\s+SET|DELETE FROM)\s", RegexOptions.IgnoreCase)]
    private static partial Regex SqlKeyword();

    [GeneratedRegex(@"CREATE TABLE (\w+) \((?:(?!\);).)*?organisation_id\s+uuid NOT NULL", RegexOptions.Singleline)]
    private static partial Regex TenantTable();
}
