using System.Data.Common;
using MimeKit;
using MimeKit.Cryptography;
using Microsoft.Data.Sqlite;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Utilities.Collections;
using Org.BouncyCastle.X509;

namespace Mailbox.Security.Smime;

/// <summary>
/// This machine's S/MIME certificates, in a file beside everything else this application keeps.
/// </summary>
/// <remarks>
/// <b>Why this exists rather than <c>new DefaultSecureMimeContext(path)</c>.</b> MimeKit's own
/// file-name constructor looks for <c>System.Data.SQLite</c> by reflection and throws
/// <see cref="NotSupportedException"/> — "SQLite is not available" — when it cannot find it. This
/// tree carries <c>Microsoft.Data.Sqlite</c>, which is a different package that check does not
/// see, so every attempt to sign, encrypt, verify or decrypt with S/MIME threw before it began.
/// Nothing caught it because every test builds a <c>TemporarySecureMimeContext</c> instead, and a
/// temporary context proves nothing about the store the application actually opens.
/// <para>
/// The repair takes the constructor MimeKit provides for exactly this — a database over a
/// connection the caller brings — so no second SQLite provider is added to the build and the
/// packaging does not change. One adapter is needed on top of it: <c>Microsoft.Data.Sqlite</c>
/// refuses a parameter whose <see cref="DbParameter.Value"/> was never set, where the provider
/// MimeKit was written against treated it as <c>NULL</c>. Four of the certificate columns are
/// nullable, so without the adapter the first import fails with "Value must be set".
/// </para>
/// <para>
/// <b>And it is a path rather than a password.</b> <c>DefaultSecureMimeContext</c> has no
/// single-argument file-name constructor: <c>new DefaultSecureMimeContext(somePath)</c> binds to
/// the one that takes a <em>password</em>, so the store would have been made in the library's own
/// home directory with the path string as the key that encrypts the private keys in it.
/// </para>
/// </remarks>
public static class CertificateStore
{
    /// <summary>What the file is called, inside whichever directory it is asked for.</summary>
    public const string FileName = "certificates.db";

    /// <summary>
    /// The passphrase the private keys in the file are wrapped with.
    /// </summary>
    /// <remarks>
    /// Fixed, and openly so. The file sits in the reader's own home directory beside the mail it
    /// protects, and a constant in a GPL binary is not a secret — what it buys is that the key
    /// material is not lying in the clear inside a database anything can open. Encrypting the
    /// store for real is a different piece of work with a passphrase prompt at launch behind it,
    /// and it is on the queue as "Encrypting the local store"; pretending this is that would be
    /// worse than saying plainly what it is.
    /// </remarks>
    private const string DatabaseKey = "mailbox";

    /// <summary>Opens — creating on first use — the certificate store in a directory.</summary>
    public static SecureMimeContext Open(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);

        Directory.CreateDirectory(directory);

        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, FileName) }.ToString());

        return new DefaultSecureMimeContext(new SqliteDatabase(connection, DatabaseKey));
    }

    /// <summary>
    /// MimeKit's SQLite certificate database, with the one difference this provider needs.
    /// </summary>
    /// <remarks>
    /// <c>Microsoft.Data.Sqlite</c> throws <see cref="InvalidOperationException"/> ("Value must be
    /// set") for a parameter left at <see langword="null"/>, rather than binding it as
    /// <c>NULL</c>. Every command MimeKit builds passes through here on its way to being run, and
    /// an unset value becomes <see cref="DBNull.Value"/>, which both providers agree about.
    /// </remarks>
    private sealed class SqliteDatabase(DbConnection connection, string password)
        : SqliteCertificateDatabase(connection, password)
    {
        private static DbCommand Bound(DbCommand command)
        {
            foreach (DbParameter parameter in command.Parameters)
            {
                parameter.Value ??= DBNull.Value;
            }

            return command;
        }

        protected override DbCommand GetInsertCommand(DbConnection connection, X509CertificateRecord record)
            => Bound(base.GetInsertCommand(connection, record));

        protected override DbCommand GetInsertCommand(DbConnection connection, X509CrlRecord record)
            => Bound(base.GetInsertCommand(connection, record));

        protected override DbCommand GetUpdateCommand(
            DbConnection connection, X509CertificateRecord record, X509CertificateRecordFields fields)
            => Bound(base.GetUpdateCommand(connection, record, fields));

        protected override DbCommand GetDeleteCommand(DbConnection connection, X509CertificateRecord record)
            => Bound(base.GetDeleteCommand(connection, record));

        protected override DbCommand GetDeleteCommand(DbConnection connection, X509CrlRecord record)
            => Bound(base.GetDeleteCommand(connection, record));

        protected override DbCommand GetSelectCommand(
            DbConnection connection, X509Certificate certificate, X509CertificateRecordFields fields)
            => Bound(base.GetSelectCommand(connection, certificate, fields));

        protected override DbCommand GetSelectCommand(
            DbConnection connection, MailboxAddress mailbox, DateTime now,
            bool requirePrivateKey, X509CertificateRecordFields fields)
            => Bound(base.GetSelectCommand(connection, mailbox, now, requirePrivateKey, fields));

        protected override DbCommand GetSelectCommand(
            DbConnection connection, ISelector<X509Certificate>? selector,
            bool trustedAnchorsOnly, bool requirePrivateKey, X509CertificateRecordFields fields)
            => Bound(base.GetSelectCommand(connection, selector, trustedAnchorsOnly, requirePrivateKey, fields));

        protected override DbCommand GetSelectCommand(
            DbConnection connection, X509Name issuer, X509CrlRecordFields fields)
            => Bound(base.GetSelectCommand(connection, issuer, fields));

        protected override DbCommand GetSelectCommand(
            DbConnection connection, X509Crl crl, X509CrlRecordFields fields)
            => Bound(base.GetSelectCommand(connection, crl, fields));

        protected override DbCommand GetSelectAllCrlsCommand(DbConnection connection)
            => Bound(base.GetSelectAllCrlsCommand(connection));
    }
}
