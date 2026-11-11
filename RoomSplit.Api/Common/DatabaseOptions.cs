using System.ComponentModel.DataAnnotations;
using MySqlConnector;

namespace RoomSplit.Api.Common;

/// <summary>
/// MySQL connection fields from the <c>Database</c> configuration section (env: <c>Database__Host</c>, ...).
/// The connection string is built here so the password is escaped correctly (docs/db-connection-config.md).
/// </summary>
public class DatabaseOptions
{
    public const string SectionName = "Database";

    [Required]
    public string Host { get; set; } = "";

    [Range(1, 65535)]
    public int Port { get; set; } = 3306;

    [Required]
    public string Name { get; set; } = "";

    [Required]
    public string User { get; set; } = "";

    /// <summary>May be empty (local MySQL without a password).</summary>
    public string Password { get; set; } = "";

    /// <summary>Never log the result: it contains the password.</summary>
    public string BuildConnectionString() => new MySqlConnectionStringBuilder
    {
        Server = Host,
        Port = (uint)Port,
        Database = Name,
        UserID = User,
        Password = Password,
    }.ConnectionString;
}
