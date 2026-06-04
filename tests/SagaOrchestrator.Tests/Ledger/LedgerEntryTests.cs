using SagaOrchestrator.Ledger.Domain;

namespace SagaOrchestrator.Tests.Ledger;

public class LedgerEntryTests
{
    [Fact]
    public void Constructor_WithValidData_ShouldCreateLedgerEntry()
    {
        var accountId = Guid.NewGuid();

        var entry = new LedgerEntry(
            accountId,
            -100m,
            LedgerTransactionType.Debit,
            "Debit_saga-1",
            "Test debit");

        Assert.NotEqual(Guid.Empty, entry.Id);
        Assert.Equal(accountId, entry.AccountId);
        Assert.Equal(-100m, entry.Amount);
        Assert.Equal(LedgerTransactionType.Debit, entry.Type);
        Assert.Equal("Debit_saga-1", entry.ReferenceId);
        Assert.Equal("Test debit", entry.Description);
        Assert.NotEqual(default, entry.CreatedAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_WithEmptyReferenceId_ShouldThrowArgumentException(string referenceId)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new LedgerEntry(
                Guid.NewGuid(),
                100m,
                LedgerTransactionType.Credit,
                referenceId));

        Assert.Equal("referenceId", exception.ParamName);
    }
}