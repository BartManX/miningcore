namespace Miningcore.Blockchain.Kaspa.Configuration;

public class KaspaPaymentProcessingConfigExtra
{
    /// <summary>
    /// Password for unlocking wallet
    /// </summary>
    public string WalletPassword { get; set; }

    /// <summary>
    /// Minimum block confirmations
    /// Default: 10
    /// </summary>
    public int? MinimumConfirmations { get; set; }
    
    /// <summary>
    /// Maximum number of payouts which can be done in parallel
    /// Default: 2
    /// </summary>
    public int? MaxDegreeOfParallelPayouts { get; set; }
}