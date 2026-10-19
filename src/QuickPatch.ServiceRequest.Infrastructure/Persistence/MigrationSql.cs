namespace QuickPatch.ServiceRequest.Infrastructure.Persistence;

/// <summary>Lee los archivos SQL de <c>Persistence/Migrations/Sql</c>, embebidos en el ensamblado.</summary>
public static class MigrationSql
{
    public static string Read(string fileName)
    {
        using var stream = typeof(MigrationSql).Assembly.GetManifestResourceStream($"Migrations.{fileName}")
            ?? throw new InvalidOperationException($"No está embebido el SQL de migración {fileName}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}