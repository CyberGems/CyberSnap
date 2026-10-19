using Microsoft.Data.Sqlite;

internal static partial class Program
{
    private static void CheckSqliteRuntime()
    {
        Assert(Environment.Version.Major == 10,
            $"desktop integration runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version()";
        var version = Version.Parse((string)command.ExecuteScalar()!);
        Assert(version >= new Version(3, 50, 2),
            $"native SQLite {version} includes the fix for CVE-2025-6965");

        command.CommandText = "CREATE VIRTUAL TABLE documents USING fts5(content)";
        command.ExecuteNonQuery();
        command.CommandText = "INSERT INTO documents(content) VALUES ($english), ($spanish)";
        command.Parameters.AddWithValue("$english", "Screenshot with annotations");
        command.Parameters.AddWithValue("$spanish", "Captura con anotaciones y traducción");
        command.ExecuteNonQuery();
        command.Parameters.Clear();
        command.CommandText = "SELECT count(*) FROM documents WHERE documents MATCH 'annotations OR traducción'";
        Assert((long)command.ExecuteScalar()! == 2,
            "SQLite FTS5 indexes and searches English and Spanish text");
    }
}
