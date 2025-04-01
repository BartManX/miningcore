using Miningcore.Native;
using static Miningcore.Native.Cryptonight.Algorithm;

namespace Miningcore.Crypto.Hashing.Algorithms;

[Identifier("flex")]
public class Flex : IHashAlgorithm
{
    public void Digest(ReadOnlySpan<byte> data, Span<byte> result, params object[] extra)
    {
        Cryptonight.CryptonightHash(data, result, FLEX_KCN, 0);
    }
}
