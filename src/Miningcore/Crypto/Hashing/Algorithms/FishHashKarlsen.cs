using Miningcore.Contracts;
using Miningcore.Native;

namespace Miningcore.Crypto.Hashing.Algorithms;

[Identifier("fishhashkarlsen")]
public unsafe class FishHashKarlsen : IHashAlgorithm
{
    private bool enableFishHashPlus = false;
    private IntPtr handle = IntPtr.Zero;
    private readonly object genLock = new();

    public FishHashKarlsen(bool enableFishHashPlus = false, bool fullContext = false, uint threads = 4)
    {
        this.enableFishHashPlus = enableFishHashPlus;

        lock(genLock)
        {
            this.handle = Multihash.fishhashGetContext(fullContext);
            if(fullContext)
                Multihash.fishhashPrebuildDataset(this.handle, threads);
        }
    }

    public void Digest(ReadOnlySpan<byte> data, Span<byte> result, params object[] extra)
    {
        Contract.Requires<ArgumentException>(this.handle != IntPtr.Zero);
        Contract.Requires<ArgumentException>(result.Length >= 32);

        // concat data in byte[64]
        Span<byte> seedBytes = stackalloc byte[64];
        data.CopyTo(seedBytes);

        var mixHash = new Multihash.Fishhash_hash256();

        var seed = new Multihash.Fishhash_hash512();
        seed.bytes = seedBytes.ToArray();

        mixHash = (this.enableFishHashPlus) ? Multihash.fishhashplusKernel(this.handle, ref seed) : Multihash.fishhashKernel(this.handle, ref seed);

        fixed(byte* input = mixHash.bytes)
        {
            fixed (byte* output = result)
            {
                Multihash.blake3(input, output, (uint) mixHash.bytes.Length, null, 0);
            }
        }
    }
}
