namespace SagaOrchestrator.Ledger.Domain;

public enum LedgerTransactionType
{
    Debit = 1,
    Credit = 2,
    AbortMarker = 99 // 💀 Tombstone
}