using SagaOrchestrator.Ledger.Domain;

namespace SagaOrchestrator.Tests.Ledger;

public class AccountTests
{
    [Fact]
    public void Constructor_WithValidData_ShouldCreateActiveAccountWithZeroBalance()
    {
        var accountId = Guid.NewGuid();

        var account = new Account(accountId, "Sender");

        Assert.Equal(accountId, account.Id);
        Assert.Equal("Sender", account.Name);
        Assert.Equal(0m, account.Balance);
        Assert.True(account.IsActive);
        Assert.NotEqual(default, account.CreatedAt);
    }

    [Fact]
    public void Constructor_WithEmptyId_ShouldThrowArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new Account(Guid.Empty, "Sender"));

        Assert.Equal("id", exception.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_WithEmptyName_ShouldThrowArgumentException(string name)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new Account(Guid.NewGuid(), name));

        Assert.Equal("name", exception.ParamName);
    }

    [Fact]
    public void Credit_WithPositiveAmount_ShouldIncreaseBalance()
    {
        var account = new Account(Guid.NewGuid(), "Receiver");

        account.Credit(100m);

        Assert.Equal(100m, account.Balance);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Credit_WithNonPositiveAmount_ShouldThrowArgumentException(decimal amount)
    {
        var account = new Account(Guid.NewGuid(), "Receiver");

        var exception = Assert.Throws<ArgumentException>(() =>
            account.Credit(amount));

        Assert.Equal("amount", exception.ParamName);
    }

    [Fact]
    public void Credit_WithAmountAboveLimit_ShouldThrowArgumentException()
    {
        var account = new Account(Guid.NewGuid(), "Receiver");

        var exception = Assert.Throws<ArgumentException>(() =>
            account.Credit(Account.MaxSingleTransactionAmount + 1));

        Assert.Equal("amount", exception.ParamName);
    }

    [Fact]
    public void Debit_WithPositiveAmount_ShouldDecreaseBalance()
    {
        var account = new Account(Guid.NewGuid(), "Sender");
        account.Credit(100m);

        account.Debit(30m);

        Assert.Equal(70m, account.Balance);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Debit_WithNonPositiveAmount_ShouldThrowArgumentException(decimal amount)
    {
        var account = new Account(Guid.NewGuid(), "Sender");

        var exception = Assert.Throws<ArgumentException>(() =>
            account.Debit(amount));

        Assert.Equal("amount", exception.ParamName);
    }

    [Fact]
    public void Debit_WithAmountAboveLimit_ShouldThrowArgumentException()
    {
        var account = new Account(Guid.NewGuid(), "Sender");

        var exception = Assert.Throws<ArgumentException>(() =>
            account.Debit(Account.MaxSingleTransactionAmount + 1));

        Assert.Equal("amount", exception.ParamName);
    }

    [Fact]
    public void Debit_WhenBalanceWouldGoBelowOverdraftLimit_ShouldThrowInvalidOperationException()
    {
        var account = new Account(Guid.NewGuid(), "Sender");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            account.Debit(Math.Abs(Account.OverdraftLimit) + 1));

        Assert.Equal("Insufficient funds", exception.Message);
    }

    [Fact]
    public void Debit_WhenBalanceStaysAtOverdraftLimit_ShouldSucceed()
    {
        var account = new Account(Guid.NewGuid(), "Sender");

        account.Debit(Math.Abs(Account.OverdraftLimit));

        Assert.Equal(Account.OverdraftLimit, account.Balance);
    }

    [Fact]
    public void Deactivate_WhenActive_ShouldDeactivateAccount()
    {
        var account = new Account(Guid.NewGuid(), "Sender");

        account.Deactivate();

        Assert.False(account.IsActive);
    }

    [Fact]
    public void Deactivate_WhenAlreadyInactive_ShouldThrowInvalidOperationException()
    {
        var account = new Account(Guid.NewGuid(), "Sender");
        account.Deactivate();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            account.Deactivate());

        Assert.Equal("Account is already inactive", exception.Message);
    }

    [Fact]
    public void Activate_WhenInactive_ShouldActivateAccount()
    {
        var account = new Account(Guid.NewGuid(), "Sender");
        account.Deactivate();

        account.Activate();

        Assert.True(account.IsActive);
    }

    [Fact]
    public void Activate_WhenAlreadyActive_ShouldThrowInvalidOperationException()
    {
        var account = new Account(Guid.NewGuid(), "Sender");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            account.Activate());

        Assert.Equal("Account is already active", exception.Message);
    }
}