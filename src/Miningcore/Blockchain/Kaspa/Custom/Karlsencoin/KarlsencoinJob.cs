using Miningcore.Contracts;
using Miningcore.Crypto;
using Miningcore.Crypto.Hashing.Algorithms;

namespace Miningcore.Blockchain.Kaspa.Custom.Karlsencoin;

public class KarlsencoinJob : KaspaJob
{
    public KarlsencoinJob(IHashAlgorithm customBlockHeaderHasher, IHashAlgorithm customCoinbaseHasher, IHashAlgorithm customShareHasher) : base(customBlockHeaderHasher, customCoinbaseHasher, customShareHasher)
    {
    }
}