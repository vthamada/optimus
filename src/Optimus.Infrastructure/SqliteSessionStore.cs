using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Optimus.Application;
using Optimus.Domain;

namespace Optimus.Infrastructure;

public sealed class SqliteSessionStore(string databasePath) : ISessionStore
{
    private const int ApplicationId = 0x4F505449;
    private const int SchemaVersion = 1;
    public const int MaximumSessionBytes = 4 * 1024 * 1024;
    private readonly string _path = Path.GetFullPath(databasePath);
    private readonly SemaphoreSlim _initialization = new(1, 1);

    // Microsoft.Data.Sqlite async methods execute synchronously; the whole connection belongs to a worker.
    public Task<Result<bool>> SaveAsync(DiagnosticSession session, CancellationToken cancellationToken) =>
        Task.Run(() => SaveCoreAsync(session, cancellationToken), cancellationToken);

    private async Task<Result<bool>> SaveCoreAsync(DiagnosticSession session, CancellationToken cancellationToken)
    {
        try
        {
            if (!SessionValidation.IsValid(session)) return Result<bool>.Failure(ErrorCode.InvalidData, "A sessão contém identidade, fatos ou referências inválidas.");
            using var buffer = new BoundedBuffer(MaximumSessionBytes);
            await JsonSerializer.SerializeAsync(buffer, session, DiagnosticJson.Options, cancellationToken).ConfigureAwait(false);
            var json = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO sessions (id, started_at, status, finding_count, payload) VALUES ($id, $started, $status, $count, $payload)";
            command.Parameters.AddWithValue("$id", session.Id.ToString());
            command.Parameters.AddWithValue("$started", session.StartedAt.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("$status", session.Status.ToString());
            command.Parameters.AddWithValue("$count", session.Findings.Count);
            command.Parameters.AddWithValue("$payload", json);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return Result<bool>.Success(true);
        }
        catch (Exception error) when (IsStorageError(error))
        {
            return Result<bool>.Failure(Code(error), error.Message);
        }
    }

    public Task<Result<IReadOnlyList<SessionSummary>>> ListAsync(CancellationToken cancellationToken) =>
        Task.Run(() => ListCoreAsync(cancellationToken), cancellationToken);

    private async Task<Result<IReadOnlyList<SessionSummary>>> ListCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, started_at, status, finding_count FROM sessions ORDER BY started_at DESC LIMIT 20";
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var result = new List<SessionSummary>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.GetValue(0) is not string idText || !Guid.TryParse(idText, out var id) || id == Guid.Empty
                    || reader.GetValue(1) is not string startedText
                    || !DateTimeOffset.TryParse(startedText, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var started)
                    || reader.GetValue(2) is not string statusText || !Enum.TryParse<SessionStatus>(statusText, out var status)
                    || !Enum.IsDefined(status) || reader.GetValue(3) is not long findingCount || findingCount is < 0 or > int.MaxValue)
                    throw new InvalidDataException("O histórico contém metadados inválidos; o banco foi preservado.");
                result.Add(new(id, started, status, checked((int)findingCount)));
            }

            return Result<IReadOnlyList<SessionSummary>>.Success(result);
        }
        catch (Exception error) when (IsStorageError(error))
        {
            return Result<IReadOnlyList<SessionSummary>>.Failure(Code(error), error.Message);
        }
    }

    public Task<Result<DiagnosticSession>> GetAsync(Guid id, CancellationToken cancellationToken) =>
        Task.Run(() => GetCoreAsync(id, cancellationToken), cancellationToken);

    private async Task<Result<DiagnosticSession>> GetCoreAsync(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT length(CAST(payload AS BLOB)), CASE WHEN length(CAST(payload AS BLOB)) <= $limit THEN payload ELSE NULL END, typeof(payload) FROM sessions WHERE id = $id";
            command.Parameters.AddWithValue("$id", id.ToString());
            command.Parameters.AddWithValue("$limit", MaximumSessionBytes);
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return Result<DiagnosticSession>.Failure(ErrorCode.InvalidData, "A sessão solicitada não foi encontrada.");
            if (reader.GetString(2) != "text") throw new InvalidDataException("A sessão armazenada não contém um documento JSON textual válido.");
            if (reader.GetInt64(0) > MaximumSessionBytes) throw new InvalidDataException("A sessão excede o limite de leitura de 4 MiB.");
            var payload = reader.GetString(1);
            var session = JsonSerializer.Deserialize<DiagnosticSession>(payload, DiagnosticJson.Options);
            if (session is null || session.Id != id || !SessionValidation.IsValid(session)) throw new InvalidDataException("A sessão contém fatos ou referências inválidas; o banco foi preservado.");
            return Result<DiagnosticSession>.Success(session);
        }
        catch (Exception error) when (IsStorageError(error))
        {
            return Result<DiagnosticSession>.Failure(Code(error), error.Message);
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        await _initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection? connection = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            if (!File.Exists(_path)) await InitializeNewDatabaseAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            connection = new(new SqliteConnectionStringBuilder
            {
                DataSource = _path,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
                DefaultTimeout = 5
            }.ToString());
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            using var identity = connection.CreateCommand();
            identity.CommandText = "PRAGMA application_id";
            var owner = Convert.ToInt32(await identity.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
            if (owner != ApplicationId) throw new DatabaseIdentityException("O arquivo não é um banco reconhecido do opTImus. Ele não foi alterado.");
            identity.CommandText = "PRAGMA user_version";
            var version = Convert.ToInt32(await identity.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
            if (version != SchemaVersion) throw new DatabaseVersionException($"Versão de banco não suportada: {version}. O banco foi preservado.");
            return connection;
        }
        catch
        {
            connection?.Dispose();
            throw;
        }
        finally { _initialization.Release(); }
    }

    private async Task InitializeNewDatabaseAsync()
    {
        var staging = Path.Combine(Path.GetDirectoryName(_path)!, $".optimus-db-{Guid.NewGuid():N}.tmp");
        bool owned = false;
        Exception? primaryError = null;
        try
        {
            using (var reservation = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { owned = true; }
            using (var initializer = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = staging,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
                DefaultTimeout = 5
            }.ToString()))
            {
                // This bounded ownership transaction cannot leave the published database half initialized.
                await initializer.OpenAsync(CancellationToken.None).ConfigureAwait(false);
                using var transaction = initializer.BeginTransaction();
                using var initialize = initializer.CreateCommand();
                initialize.Transaction = transaction;
                initialize.CommandText = $"""
                    PRAGMA application_id = {ApplicationId};
                    CREATE TABLE sessions (
                        id TEXT PRIMARY KEY NOT NULL, started_at TEXT NOT NULL, status TEXT NOT NULL,
                        finding_count INTEGER NOT NULL CHECK (finding_count BETWEEN 0 AND 2147483647),
                        payload TEXT NOT NULL CHECK (json_valid(payload))
                    ) STRICT;
                    PRAGMA user_version = {SchemaVersion};
                    """;
                await initialize.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
                transaction.Commit();
            }
            try { File.Move(staging, _path, overwrite: false); owned = false; }
            catch (IOException) when (File.Exists(_path)) { /* Another owner published; validate that database next. */ }
        }
        catch (Exception error) { primaryError = error; }

        if (owned)
        {
            try { File.Delete(staging); }
            catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"{primaryError?.Message ?? "A publicação não ocorreu."} Limpeza própria pendente: {staging}",
                    primaryError is null ? cleanupError : new AggregateException(primaryError, cleanupError));
            }
        }
        if (primaryError is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primaryError).Throw();
    }

    private static bool IsStorageError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or SqliteException or JsonException;
    private static ErrorCode Code(Exception error) => error switch
    {
        DatabaseIdentityException => ErrorCode.UnrecognizedDatabase,
        DatabaseVersionException => ErrorCode.UnsupportedSchema,
        InvalidDataException or JsonException => ErrorCode.InvalidData,
        UnauthorizedAccessException => ErrorCode.PermissionDenied,
        _ => ErrorCode.StorageFailure
    };

    private sealed class DatabaseIdentityException(string message) : IOException(message);
    private sealed class DatabaseVersionException(string message) : IOException(message);

    private sealed class BoundedBuffer(int maximum) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Check(buffer.Length);
            base.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }
        private void Check(int length)
        {
            if (Position + length > maximum) throw new InvalidDataException("A sessão excede o limite de armazenamento de 4 MiB.");
        }
    }
}
