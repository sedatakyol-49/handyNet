using System.Data.Common;
using HandyNet.Modules.Pos.Application;
using HandyNet.Modules.Pos.Domain;
using HandyNet.Persistence;
using HandyNet.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace HandyNet.Modules.Pos.Infrastructure;

/// <summary>
/// HandyNet's simulated cloud TSE (test only, not certified). Behaves like a cloud TSE: one TSE per organization with a
/// serial number (SHA-256 of its public key), registers as clients, transactions started and finished with monotonic
/// transaction numbers and signature counters, ECDSA signatures (P-256/P-384, plain format) over the process data, log
/// times and a QR code payload. Admin-configurable faults: latency, transient failure rate (either before or after the
/// call was processed — a lost response), forced outage. Start and finish are idempotent per transaction key.
/// <para>
/// Unlike a real TSE its state lives in the caller's database transaction: a rolled-back sale also rolls back its TSE
/// transaction, so test data stays consistent with the receipts.
/// </para>
/// </summary>
public sealed class SimulatedTseSigner(
    ISimulatedTseStore store,
    IFiscalSecretProtector protector,
    ISimulatedTseFaults faults,
    IClock clock,
    Guid organizationId,
    SimulatedTseBehaviour behaviour) : IFiscalSigner
{
    public FiscalMode Mode => FiscalMode.SimulatedTse;

    public async Task<Result<FiscalTransaction>> StartTransactionAsync(FiscalTransactionRequest request, CancellationToken ct)
    {
        var (failure, failAfter) = await FaultsAsync(ct);
        if (failure is not null)
            return failure;

        var tse = await store.LockAsync(organizationId, behaviour.Algorithm, ct);
        var existing = await store.FindAsync(tse.Id, request.TransactionKey, ct);
        if (existing is null)
        {
            var now = clock.UtcNow;
            var number = tse.TransactionCounter + 1;
            var counter = tse.SignatureCounter + 1;
            var signature = SimulatedTseCrypto.Sign(PrivateKey(tse), tse.Algorithm,
                new TseLogMessage(TseLogMessage.StartOperation, tse.SerialNumber, request.ClientId, number, counter, now, request.ProcessType, ""));
            existing = new SimulatedTseTxRow(Guid.CreateVersion7(), tse.Id, request.TransactionKey, request.ClientId, number,
                SimulatedTseTransactionState.Active, now, counter, signature);
            await store.StartAsync(tse with { TransactionCounter = number, SignatureCounter = counter }, existing, ct);
        }

        if (failAfter)
            return LostResponse();
        return new FiscalTransaction(existing.TransactionKey, existing.ClientId, existing.Number, existing.StartedAt, tse.SerialNumber);
    }

    public async Task<Result<FiscalData>> FinishTransactionAsync(FiscalTransaction transaction, FiscalProcess process, CancellationToken ct)
    {
        var (failure, failAfter) = await FaultsAsync(ct);
        if (failure is not null)
            return failure;

        var tse = await store.LockAsync(organizationId, behaviour.Algorithm, ct);
        var row = await store.FindAsync(tse.Id, transaction.TransactionKey, ct);
        if (row is null)
            return FiscalErrors.Refused("Test-TSE: unknown transaction.");
        if (row.State == SimulatedTseTransactionState.Finished)
        {
            // Retry of a finish that was already processed: return the stored signature, never sign twice.
            if (row.ProcessType != process.ProcessType || row.ProcessData != process.ProcessData)
                return FiscalErrors.Refused("Test-TSE: the transaction was already finished with other process data.");
        }
        else
        {
            var now = clock.UtcNow;
            var counter = tse.SignatureCounter + 1;
            var signature = SimulatedTseCrypto.Sign(PrivateKey(tse), tse.Algorithm,
                new TseLogMessage(TseLogMessage.FinishOperation, tse.SerialNumber, row.ClientId, row.Number, counter, now, process.ProcessType,
                    process.ProcessData));
            row = row with
            {
                State = SimulatedTseTransactionState.Finished,
                FinishedAt = now,
                ProcessType = process.ProcessType,
                ProcessData = process.ProcessData,
                SignatureCounter = counter,
                Signature = signature,
            };
            await store.FinishAsync(tse with { SignatureCounter = counter }, row, ct);
        }

        if (failAfter)
            return LostResponse();
        return ToFiscalData(tse, row);
    }

    internal static FiscalData ToFiscalData(SimulatedTseState tse, SimulatedTseTxRow row) => new(
        FiscalStatus.Signed,
        KassenSichV.TestTseNotice,
        tse.SerialNumber,
        row.Number,
        row.SignatureCounter,
        row.Signature,
        row.StartedAt,
        row.FinishedAt,
        Mode: nameof(FiscalMode.SimulatedTse),
        IsTest: true,
        ClientId: row.ClientId,
        ProcessType: row.ProcessType,
        ProcessData: row.ProcessData,
        SignatureAlgorithm: tse.Algorithm,
        TimeFormat: tse.TimeFormat,
        PublicKey: tse.PublicKey,
        QrPayload: KassenSichV.QrPayload(row.ClientId, row.ProcessType!, row.ProcessData!, row.Number, row.SignatureCounter!.Value, row.StartedAt,
            row.FinishedAt!.Value, tse.Algorithm, tse.TimeFormat, row.Signature!, tse.PublicKey));

    private byte[] PrivateKey(SimulatedTseState tse) =>
        Convert.FromBase64String(protector.Unprotect(tse.ProtectedPrivateKey)
                                 ?? throw new InvalidOperationException("The simulated TSE key cannot be decrypted (Data Protection key ring changed)."));

    /// <summary>Latency, forced outage and the dice for a transient failure (before processing, or after it: response lost).</summary>
    private async Task<(Error? Failure, bool FailAfter)> FaultsAsync(CancellationToken ct)
    {
        if (behaviour.LatencyMs > 0)
            await Task.Delay(behaviour.LatencyMs, ct);
        if (behaviour.ForcedOutage)
            return (FiscalErrors.Down("Test-TSE: simulated outage (TSE ausgefallen)."), false);
        if (!faults.ShouldFail(behaviour.FailureRatePercent))
            return (null, false);
        return faults.FailAfterProcessing()
            ? (null, true)
            : (FiscalErrors.Transient("Test-TSE: simulated failure (request not processed)."), false);
    }

    private static Error LostResponse() => FiscalErrors.Transient("Test-TSE: simulated failure (processed, response lost).");
}

/// <summary>SQL persistence of the simulated TSE inside the current EF transaction (raw commands: nothing else is flushed).</summary>
internal sealed class SqlSimulatedTseStore(HandyNetDbContext db, IFiscalSecretProtector protector, IClock clock) : ISimulatedTseStore
{
    private const string Columns =
        "id, organization_id, serial_number, public_key, signature_algorithm, time_format, transaction_counter, signature_counter, protected_private_key";

    public async Task<SimulatedTseState> LockAsync(Guid organizationId, string algorithmForNewTse, CancellationToken ct)
    {
        if (await LockExistingAsync(organizationId, ct) is { } existing)
            return existing;
        var keys = SimulatedTseCrypto.Generate(algorithmForNewTse);
        await ExecuteAsync($"""
            INSERT INTO pos.simulated_tses ({Columns}, created_at)
            VALUES (@id, @org, @serial, @key, @alg, @fmt, 0, 0, @priv, @now)
            ON CONFLICT (organization_id) DO NOTHING
            """, ct, ("id", Guid.CreateVersion7()), ("org", organizationId), ("serial", keys.SerialNumber), ("key", keys.PublicKey),
            ("alg", algorithmForNewTse), ("fmt", KassenSichV.TimeFormatUnix), ("priv", protector.Protect(Convert.ToBase64String(keys.Pkcs8PrivateKey))),
            ("now", clock.UtcNow));
        return await LockExistingAsync(organizationId, ct) ?? throw new InvalidOperationException("Simulated TSE could not be created.");
    }

    public async Task<SimulatedTseTxRow?> FindAsync(Guid tseId, Guid transactionKey, CancellationToken ct)
    {
        await using var reader = await ReadAsync("""
            SELECT id, tse_id, transaction_key, client_id, number, state, started_at, start_signature_counter, start_signature,
                   finished_at, process_type, process_data, signature_counter, signature
            FROM pos.simulated_tse_transactions WHERE tse_id = @tse AND transaction_key = @key
            """, ct, ("tse", tseId), ("key", transactionKey));
        if (!await reader.ReadAsync(ct))
            return null;
        return new SimulatedTseTxRow(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetString(3), reader.GetInt64(4),
            Enum.Parse<SimulatedTseTransactionState>(reader.GetString(5)), reader.GetFieldValue<DateTimeOffset>(6), reader.GetInt64(7), reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9), reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11), reader.IsDBNull(12) ? null : reader.GetInt64(12), reader.IsDBNull(13) ? null : reader.GetString(13));
    }

    public async Task StartAsync(SimulatedTseState tse, SimulatedTseTxRow transaction, CancellationToken ct)
    {
        await ExecuteAsync("""
            INSERT INTO pos.simulated_tse_transactions (id, organization_id, tse_id, transaction_key, client_id, number, state, started_at,
                start_signature_counter, start_signature)
            VALUES (@id, @org, @tse, @key, @client, @number, 'Active', @started, @counter, @signature)
            """, ct, ("id", transaction.Id), ("org", tse.OrganizationId), ("tse", tse.Id), ("key", transaction.TransactionKey), ("client", transaction.ClientId),
            ("number", transaction.Number), ("started", transaction.StartedAt), ("counter", transaction.StartSignatureCounter),
            ("signature", transaction.StartSignature));
        await CountersAsync(tse, ct);
    }

    public async Task FinishAsync(SimulatedTseState tse, SimulatedTseTxRow transaction, CancellationToken ct)
    {
        var updated = await ExecuteAsync("""
            UPDATE pos.simulated_tse_transactions
            SET state = 'Finished', finished_at = @finished, process_type = @type, process_data = @data, signature_counter = @counter, signature = @signature
            WHERE id = @id AND state = 'Active'
            """, ct, ("id", transaction.Id), ("finished", transaction.FinishedAt), ("type", transaction.ProcessType), ("data", transaction.ProcessData),
            ("counter", transaction.SignatureCounter), ("signature", transaction.Signature));
        if (updated != 1)
            throw new InvalidOperationException("The simulated TSE transaction is not active.");
        await CountersAsync(tse, ct);
    }

    private Task CountersAsync(SimulatedTseState tse, CancellationToken ct) => ExecuteAsync(
        "UPDATE pos.simulated_tses SET transaction_counter = @tx, signature_counter = @sig WHERE id = @id",
        ct, ("id", tse.Id), ("tx", tse.TransactionCounter), ("sig", tse.SignatureCounter));

    private async Task<SimulatedTseState?> LockExistingAsync(Guid organizationId, CancellationToken ct)
    {
        await using var reader = await ReadAsync($"SELECT {Columns} FROM pos.simulated_tses WHERE organization_id = @org FOR UPDATE", ct, ("org", organizationId));
        if (!await reader.ReadAsync(ct))
            return null;
        return new SimulatedTseState(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5),
            reader.GetInt64(6), reader.GetInt64(7), reader.GetString(8));
    }

    private async Task<int> ExecuteAsync(string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await using var command = await CommandAsync(sql, parameters, ct);
        return await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<DbDataReader> ReadAsync(string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        var command = await CommandAsync(sql, parameters, ct);
        return await command.ExecuteReaderAsync(System.Data.CommandBehavior.Default, ct);
    }

    private async Task<DbCommand> CommandAsync(string sql, (string Name, object? Value)[] parameters, CancellationToken ct)
    {
        var transaction = db.Database.CurrentTransaction
                          ?? throw new InvalidOperationException("The simulated TSE runs inside the receipt transaction.");
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await db.Database.OpenConnectionAsync(ct);
        var command = connection.CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
        return command;
    }
}
