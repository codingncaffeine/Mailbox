using Mailbox.Store;
using Microsoft.Data.Sqlite;

namespace Mailbox.Tests;

/// <summary>
/// Encrypting a profile's databases, and getting them back.
/// </summary>
/// <remarks>
/// The most destructive thing this application can do to somebody's mail is rewrite every page of
/// the file it is in, so this is tested harder than the size of the code suggests: that the
/// contents survive both directions, that the schema version survives, that a full-text index
/// still finds what it found, that a wrong key opens nothing, and that a profile half way through
/// the change finishes rather than failing.
/// </remarks>
public class StoreEncryptionTests : IDisposable
{
    private readonly string _profile;

    public StoreEncryptionTests()
    {
        _profile = Path.Combine(Path.GetTempPath(), $"mailbox-crypt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_profile, "accounts"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_profile, recursive: true);
        }
        catch (IOException)
        {
            // A temporary profile the operating system can clean up.
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>A database with a little of everything a real one has.</summary>
    private string Make(string name, int rows = 3)
    {
        var path = Path.Combine(_profile, "accounts", name);

        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ToString());

        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "PRAGMA journal_mode=WAL;"
            + "CREATE TABLE messages(id INTEGER PRIMARY KEY, subject TEXT);"
            + "CREATE VIRTUAL TABLE search USING fts5(subject);"
            + "PRAGMA user_version=37;";
        command.ExecuteNonQuery();

        for (var i = 0; i < rows; i++)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText =
                "INSERT INTO messages(subject) VALUES($s); INSERT INTO search(subject) VALUES($s)";
            insert.Parameters.AddWithValue("$s", $"The quarterly figures {i}");
            insert.ExecuteNonQuery();
        }

        return path;
    }

    private static (long Rows, long Found, long Version) Read(string path, byte[]? key)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString());

        connection.Open();

        if (key is not null)
        {
            using var unlock = connection.CreateCommand();
            unlock.CommandText = $"PRAGMA key = \"x'{Convert.ToHexString(key)}'\"";
            unlock.ExecuteNonQuery();
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT (SELECT count(*) FROM messages), "
            + "(SELECT count(*) FROM search WHERE search MATCH 'quarterly'), "
            + "(SELECT * FROM pragma_user_version)";

        using var reader = command.ExecuteReader();
        reader.Read();
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    private static bool LeaksPlaintext(string path)
        => System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(path))
            .Contains("quarterly", StringComparison.Ordinal);

    // ---- There and back ------------------------------------------------------------------------

    [Fact]
    public void AProfileIsEncryptedAndStillHoldsEverythingItHeld()
    {
        var path = Make("you@example.com.db");
        Assert.True(LeaksPlaintext(path));

        var key = StoreKey.Make();
        var result = StoreEncryption.Encrypt(_profile, key);

        Assert.True(result.Worked, result.Problem);
        Assert.Contains(path, result.Changed);
        Assert.False(LeaksPlaintext(path));
        Assert.True(StoreEncryption.IsEncrypted(path));

        // The rows, the full-text index and the schema version: all three, because losing the
        // last one silently would migrate the store from the beginning on the next start.
        Assert.Equal((3, 3, 37), Read(path, key));
    }

    [Fact]
    public void AndDecryptedAgain()
    {
        var path = Make("you@example.com.db");
        var key = StoreKey.Make();

        Assert.True(StoreEncryption.Encrypt(_profile, key).Worked);
        Assert.True(StoreEncryption.Decrypt(_profile, key).Worked);

        Assert.False(StoreEncryption.IsEncrypted(path));
        Assert.Equal((3, 3, 37), Read(path, null));
    }

    /// <summary>
    /// Every database, not just the mail: an address book left in the clear beside encrypted mail
    /// is the half-measure that reads as a feature and is not one.
    /// </summary>
    [Fact]
    public void EveryDatabaseInTheProfileIsRewritten()
    {
        Make("you@example.com.db");
        Make("work@example.net.db");

        var pim = Path.Combine(_profile, "pim.db");
        File.Copy(Make("staging.db"), pim);
        File.Delete(Path.Combine(_profile, "accounts", "staging.db"));

        var key = StoreKey.Make();
        var result = StoreEncryption.Encrypt(_profile, key);

        Assert.True(result.Worked, result.Problem);
        Assert.Equal(3, result.Changed.Count);
        Assert.All(StoreEncryption.Databases(_profile), p => Assert.True(StoreEncryption.IsEncrypted(p)));
    }

    // ---- What must not work --------------------------------------------------------------------

    [Fact]
    public void TheWrongKeyOpensNothing()
    {
        var path = Make("you@example.com.db");
        Assert.True(StoreEncryption.Encrypt(_profile, StoreKey.Make()).Worked);

        Assert.Throws<SqliteException>(() => Read(path, StoreKey.Make()));
        Assert.Throws<SqliteException>(() => Read(path, null));
    }

    // ---- Nothing is lost -----------------------------------------------------------------------

    /// <summary>
    /// The file that was there is still there, under a dated name, until somebody removes it.
    /// </summary>
    /// <remarks>
    /// The whole safety argument: the original is moved aside rather than deleted, and only after
    /// the replacement has been opened and checked. A failure at any point leaves a directory
    /// with a spare copy in it rather than a reader with no mail.
    /// </remarks>
    [Fact]
    public void ThePreviousFileIsKept()
    {
        Make("you@example.com.db");
        Assert.True(StoreEncryption.Encrypt(_profile, StoreKey.Make()).Worked);

        var kept = Directory.EnumerateFiles(Path.Combine(_profile, "accounts"), "*.before-*").ToList();

        Assert.Single(kept);
        Assert.False(StoreEncryption.IsEncrypted(kept[0]));
        Assert.Equal((3, 3, 37), Read(kept[0], null));
    }

    /// <summary>
    /// A profile half way through the change — an attempt that stopped — finishes rather than
    /// refusing, and never rewrites what is already the way it was asked to be.
    /// </summary>
    [Fact]
    public void AHalfDoneProfileIsFinishedRatherThanRefused()
    {
        var first = Make("you@example.com.db");
        var key = StoreKey.Make();

        Assert.True(StoreEncryption.Encrypt(_profile, key).Worked);

        // A second store arrives afterwards, in the clear — which is what an interrupted run
        // leaves behind.
        var second = Make("work@example.net.db");
        Assert.False(StoreEncryption.IsEncrypted(second));

        var again = StoreEncryption.Encrypt(_profile, key);

        Assert.True(again.Worked, again.Problem);
        Assert.Equal([second], again.Changed);
        Assert.True(StoreEncryption.IsEncrypted(first));
        Assert.True(StoreEncryption.IsEncrypted(second));
    }

    [Fact]
    public void EncryptingATwiceEncryptedProfileDoesNothingAtAll()
    {
        Make("you@example.com.db");
        var key = StoreKey.Make();

        Assert.True(StoreEncryption.Encrypt(_profile, key).Worked);
        var again = StoreEncryption.Encrypt(_profile, key);

        Assert.True(again.Worked);
        Assert.Empty(again.Changed);
    }

    /// <summary>Whatever had not been checkpointed is in the copy, not left behind in the log.</summary>
    [Fact]
    public void WritesStillInTheWriteAheadLogSurvive()
    {
        var path = Make("you@example.com.db");

        using (var connection = new SqliteConnection(
                   new SqliteConnectionStringBuilder
                   {
                       DataSource = path,
                       Mode = SqliteOpenMode.ReadWrite,
                       Pooling = false,
                   }.ToString()))
        {
            connection.Open();
            using var insert = connection.CreateCommand();
            insert.CommandText =
                "INSERT INTO messages(subject) VALUES('The quarterly figures 99');"
                + "INSERT INTO search(subject) VALUES('The quarterly figures 99')";
            insert.ExecuteNonQuery();
        }

        var key = StoreKey.Make();
        Assert.True(StoreEncryption.Encrypt(_profile, key).Worked);

        Assert.Equal((4, 4, 37), Read(path, key));
    }

    // ---- The key itself ------------------------------------------------------------------------

    [Fact]
    public void AKeyIsThirtyTwoRandomBytesAndSurvivesTheKeyring()
    {
        var key = StoreKey.Make();

        Assert.Equal(StoreKey.Bytes, key.Length);
        Assert.NotEqual(key, StoreKey.Make());
        Assert.Equal(key, StoreKey.Parse(StoreKey.Format(key)));
    }

    [Fact]
    public void SomethingThatIsNotAKeyIsNotReadAsOne()
    {
        Assert.Null(StoreKey.Parse(null));
        Assert.Null(StoreKey.Parse(string.Empty));
        Assert.Null(StoreKey.Parse("not a key"));

        // The right length and not hexadecimal, which is what a truncated or replaced keyring
        // entry looks like.
        Assert.Null(StoreKey.Parse(new string('z', StoreKey.Bytes * 2)));

        // Hexadecimal and the wrong length.
        Assert.Null(StoreKey.Parse(Convert.ToHexString(new byte[16])));
    }

    [Fact]
    public void AKeyOfTheWrongSizeIsRefusedRatherThanPadded()
    {
        Assert.Throws<ArgumentException>(() => StoreKey.Use(new byte[16]));
    }
}
