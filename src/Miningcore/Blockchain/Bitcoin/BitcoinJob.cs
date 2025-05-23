using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Security.Cryptography;
using Miningcore.Blockchain.Bitcoin.Configuration;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Configuration;
using Miningcore.Crypto;
using Miningcore.Extensions;
using Miningcore.Stratum;
using Miningcore.Time;
using Miningcore.Util;
using NBitcoin;
using NBitcoin.DataEncoders;
using Newtonsoft.Json.Linq;
using Contract = Miningcore.Contracts.Contract;
using Transaction = NBitcoin.Transaction;
using System.Numerics;

namespace Miningcore.Blockchain.Bitcoin;

public class BitcoinJob
{
    protected IHashAlgorithm blockHasher;
    protected IMasterClock clock;
    protected IHashAlgorithm coinbaseHasher;
    protected double shareMultiplier;
    protected int extraNoncePlaceHolderLength;
    protected IHashAlgorithm headerHasher;
    protected bool isPoS;
    protected string txComment;
    protected PayeeBlockTemplateExtra payeeParameters;

    protected Network network;
    protected IDestination poolAddressDestination;
    protected BitcoinTemplate coin;
    private BitcoinTemplate.BitcoinNetworkParams networkParams;
    protected readonly ConcurrentDictionary<string, bool> submissions = new(StringComparer.OrdinalIgnoreCase);
    protected uint256 blockTargetValue;
    protected byte[] coinbaseFinal;
    protected string coinbaseFinalHex;
    protected byte[] coinbaseInitial;
    protected string coinbaseInitialHex;
    protected string[] merkleBranchesHex;
    protected MerkleTree mt;
    protected string[] merkleSegwitBranchesHex;
    protected MerkleTree mtSegwit;

    ///////////////////////////////////////////
    // GetJobParams related properties

    protected object[] jobParams;
    protected string previousBlockHashReversedHex;
    protected Money rewardToPool;
    protected Transaction txOut;

    // serialization constants
    protected byte[] scriptSigFinalBytes;

    protected static byte[] sha256Empty = new byte[32];
    protected uint txVersion = 1u; // transaction version (currently 1) - see https://en.bitcoin.it/wiki/Transaction

    protected static uint txInputCount = 1u;
    protected static uint txInPrevOutIndex = (uint) (Math.Pow(2, 32) - 1);
    protected static uint txInSequence;
    protected static uint txLockTime;

    protected virtual void BuildMerkleBranches()
    {
        var transactionHashes = BlockTemplate.Transactions
            .Select(tx => (tx.TxId ?? tx.Hash)
                .HexToByteArray()
                .ReverseInPlace())
            .ToArray();

        mt = new MerkleTree(transactionHashes);

        merkleBranchesHex = mt.Steps
            .Select(x => x.ToHexString())
            .ToArray();
    }

    private static byte[] Sha256Double(byte[] input)
    {
        using (var sha256 = SHA256.Create())
        {
            byte[] hash1 = sha256.ComputeHash(input);
            byte[] hash2 = sha256.ComputeHash(hash1);
            return hash2;
        }
    }

    private MerkleTree BuildSegwitMerkleBranches()
    {
        var segwitTransactionHashes = BlockTemplate.Transactions
            .Where(tx => IsSegWitTransaction(tx))
            .Select(tx => (tx.TxId ?? tx.Hash)
                .HexToByteArray()
                .ReverseInPlace())
            .ToArray();

        // Build Merkle Tree with SegWit transactions
        return new MerkleTree(segwitTransactionHashes);
    }

    private bool IsSegWitTransaction(BitcoinBlockTransaction tx)
    {
        // Convert hex string to byte array
        byte[] txBytes = HexStringToByteArray(tx.Data);

        // Convert byte array to hex string
        string hexString = ByteArrayToHexString(txBytes);

        // Parse the transaction using NBitcoin
        var transaction = Transaction.Parse(hexString, Network.Main);

        return transaction.HasWitness;
    }

    private byte[] HexStringToByteArray(string hex)
    {
        int length = hex.Length;
        byte[] bytes = new byte[length / 2];
        for (int i = 0; i < length; i += 2)
        {
            bytes[i / 2] = Convert.ToByte(hex.Substring(i, 2), 16);
        }
        return bytes;
    }

    private string ByteArrayToHexString(byte[] bytes)
    {
        return BitConverter.ToString(bytes).Replace("-", string.Empty);
    }

    protected virtual void BuildCoinbase()
    {
        // generate script parts
        var sigScriptInitial = GenerateScriptSigInitial();
        var sigScriptInitialBytes = sigScriptInitial.ToBytes();

        var sigScriptLength = (uint) (
            sigScriptInitial.Length +
            extraNoncePlaceHolderLength +
            scriptSigFinalBytes.Length);

        // output transaction
        txOut = CreateOutputTransaction();

        // build coinbase initial
        using(var stream = new MemoryStream())
        {
            var bs = new BitcoinStream(stream, true);

            // version
            bs.ReadWrite(ref txVersion);

            // timestamp for POS coins
            if(isPoS)
            {
                var timestamp = BlockTemplate.CurTime;
                bs.ReadWrite(ref timestamp);
            }

            // serialize (simulated) input transaction
            bs.ReadWriteAsVarInt(ref txInputCount);
            bs.ReadWrite(sha256Empty);
            bs.ReadWrite(ref txInPrevOutIndex);

            // signature script initial part
            bs.ReadWriteAsVarInt(ref sigScriptLength);
            bs.ReadWrite(sigScriptInitialBytes);

            // done
            coinbaseInitial = stream.ToArray();
            coinbaseInitialHex = coinbaseInitial.ToHexString();
        }

        // build coinbase final
        using(var stream = new MemoryStream())
        {
            var bs = new BitcoinStream(stream, true);

            // signature script final part
            bs.ReadWrite(scriptSigFinalBytes);

            // tx in sequence
            bs.ReadWrite(ref txInSequence);

            // serialize output transaction
            var txOutBytes = SerializeOutputTransaction(txOut);
            bs.ReadWrite(txOutBytes);

            // misc
            bs.ReadWrite(ref txLockTime);

            // Extension point
            AppendCoinbaseFinal(bs);

            // done
            coinbaseFinal = stream.ToArray();
            coinbaseFinalHex = coinbaseFinal.ToHexString();
        }
    }

    protected virtual void AppendCoinbaseFinal(BitcoinStream bs)
    {
        if(!string.IsNullOrEmpty(txComment))
        {
            var data = Encoding.ASCII.GetBytes(txComment);
            bs.ReadWriteAsVarString(ref data);
        }

        if(coin.HasMasterNodes && !string.IsNullOrEmpty(masterNodeParameters.CoinbasePayload))
        {
            var data = masterNodeParameters.CoinbasePayload.HexToByteArray();
            bs.ReadWriteAsVarString(ref data);
        }
    }

    protected virtual byte[] SerializeOutputTransaction(Transaction tx)
    {
        var withDefaultWitnessCommitment = !string.IsNullOrEmpty(BlockTemplate.DefaultWitnessCommitment);

        var outputCount = (uint) tx.Outputs.Count;
        if(withDefaultWitnessCommitment)
            outputCount++;

        using(var stream = new MemoryStream())
        {
            var bs = new BitcoinStream(stream, true);

            // write output count
            bs.ReadWriteAsVarInt(ref outputCount);

            long amount;
            byte[] raw;
            uint rawLength;

            // serialize witness (segwit)
            if(withDefaultWitnessCommitment)
            {
                amount = 0;
                raw = BlockTemplate.DefaultWitnessCommitment.HexToByteArray();
                rawLength = (uint) raw.Length;

                if (coin.Symbol == "RVH" || coin.Symbol == "ANOK")
                {
                    // Compute witness commitment
                    raw = BlockTemplate.DefaultWitnessCommitment.HexToByteArray();
                    byte[] witnessRoot = raw;
                    byte[] witnessNonce = new byte[32];

                    // Build Merkle Tree
                    var mtSegwit = BuildSegwitMerkleBranches();
                    var merkleRoot = mtSegwit.WithFirst(new byte[32]);

                    // Concatenate witness root and nonce
                    byte[] witnessRootAndNonce = new byte[witnessRoot.Length + witnessNonce.Length];
                    Buffer.BlockCopy(witnessRoot, 0, witnessRootAndNonce, 0, witnessRoot.Length);
                    Buffer.BlockCopy(witnessNonce, 0, witnessRootAndNonce, witnessRoot.Length, witnessNonce.Length);

                    // Generate SHA256^2 hash
                    byte[] hash = Sha256Double(witnessRootAndNonce);

                    // Create scriptPubKey
                    byte[] magic = new byte[] { 0xaa, 0x21, 0xa9, 0xed };
                    byte[] scriptPubKey = new byte[36];
                    Buffer.BlockCopy(magic, 0, scriptPubKey, 0, magic.Length);
                    Buffer.BlockCopy(hash, 0, scriptPubKey, magic.Length, hash.Length);

                    raw = scriptPubKey;
                    rawLength = (uint)raw.Length;
                }

                bs.ReadWrite(ref amount);
                bs.ReadWriteAsVarInt(ref rawLength);
                bs.ReadWrite(raw);
            }

            // serialize outputs
            foreach(var output in tx.Outputs)
            {
                amount = output.Value.Satoshi;
                var outScript = output.ScriptPubKey;
                raw = outScript.ToBytes(true);
                rawLength = (uint) raw.Length;

                bs.ReadWrite(ref amount);
                bs.ReadWriteAsVarInt(ref rawLength);
                bs.ReadWrite(raw);
            }

            return stream.ToArray();
        }
    }

    protected virtual Script GenerateScriptSigInitial()
    {
        var now = ((DateTimeOffset) clock.Now).ToUnixTimeSeconds();

        // script ops
        var ops = new List<Op>();

        // push block height
        ops.Add(Op.GetPushOp(BlockTemplate.Height));

        // optionally push aux-flags
        if(!coin.CoinbaseIgnoreAuxFlags && !string.IsNullOrEmpty(BlockTemplate.CoinbaseAux?.Flags))
            ops.Add(Op.GetPushOp(BlockTemplate.CoinbaseAux.Flags.HexToByteArray()));

        // push timestamp
        ops.Add(Op.GetPushOp(now));

        // push placeholder
        ops.Add(Op.GetPushOp(0));

        return new Script(ops);
    }

    protected virtual Transaction CreateOutputTransaction()
    {
        rewardToPool = new Money(BlockTemplate.CoinbaseValue, MoneyUnit.Satoshi);
        var tx = Transaction.Create(network);

        if(coin.HasPayee)
            rewardToPool = CreatePayeeOutput(tx, rewardToPool);

        if(coin.HasMasterNodes)
            rewardToPool = CreateMasternodeOutputs(tx, rewardToPool);

        if(coin.HasFounderFee)
            rewardToPool = CreateFounderOutputs(tx, rewardToPool);

        if(coin.HasMinerDevFund)
            rewardToPool = CreateMinerDevFundOutputs(tx, rewardToPool);

        if(coin.HasMinerFund)
            rewardToPool = CreateMinerFundOutputs(tx, rewardToPool);

        if(coin.HasCommunityAddress)
            rewardToPool = CreateCommunityAddressOutputs(tx, rewardToPool);

        if(coin.HasCoinbaseDevReward)
            rewardToPool = CreateCoinbaseDevRewardOutputs(tx, rewardToPool);

        if(coin.HasFoundation)
            rewardToPool = CreateFoundationOutputs(tx, rewardToPool);

        if(coin.HasCommunity)
            rewardToPool = CreateCommunityOutputs(tx, rewardToPool);

        if(coin.HasDataMining)
            rewardToPool = CreateDataMiningOutputs(tx, rewardToPool);

        if(coin.HasDeveloper)
            rewardToPool = CreateDeveloperOutputs(tx, rewardToPool);

        // Remaining amount goes to pool
        tx.Outputs.Add(rewardToPool, poolAddressDestination);

        return tx;
    }

    protected virtual Money CreatePayeeOutput(Transaction tx, Money reward)
    {
        if(payeeParameters?.PayeeAmount != null && payeeParameters.PayeeAmount.Value > 0)
        {
            var payeeReward = new Money(payeeParameters.PayeeAmount.Value, MoneyUnit.Satoshi);
            reward -= payeeReward;

            tx.Outputs.Add(payeeReward, BitcoinUtils.AddressToDestination(payeeParameters.Payee, network));
        }

        return reward;
    }

    protected bool RegisterSubmit(string extraNonce1, string extraNonce2, string nTime, string nonce)
    {
        var key = new StringBuilder()
            .Append(extraNonce1)
            .Append(extraNonce2) // lowercase as we don't want to accept case-sensitive values as valid.
            .Append(nTime)
            .Append(nonce) // lowercase as we don't want to accept case-sensitive values as valid.
            .ToString();

        return submissions.TryAdd(key, true);
    }

    protected byte[] SerializeHeader(Span<byte> coinbaseHash, uint nTime, uint nonce, uint? versionMask, uint? versionBits)
    {
        // build merkle-root
        var merkleRoot = mt.WithFirst(coinbaseHash.ToArray());

        // Build version
        var version = BlockTemplate.Version;

        // Overt-ASIC boost
        if(versionMask.HasValue && versionBits.HasValue)
            version = (version & ~versionMask.Value) | (versionBits.Value & versionMask.Value);

#pragma warning disable 618
        var blockHeader = new BlockHeader
#pragma warning restore 618
        {
            Version = unchecked((int) version),
            Bits = new Target(Encoders.Hex.DecodeData(BlockTemplate.Bits)),
            HashPrevBlock = uint256.Parse(BlockTemplate.PreviousBlockhash),
            HashMerkleRoot = new uint256(merkleRoot),
            BlockTime = DateTimeOffset.FromUnixTimeSeconds(nTime),
            Nonce = nonce
        };

        return blockHeader.ToBytes();
    }

    protected virtual (Share Share, string BlockHex) ProcessShareInternal(
        StratumConnection worker, string extraNonce2, uint nTime, uint nonce, uint? versionBits)
    {
        var context = worker.ContextAs<BitcoinWorkerContext>();
        var extraNonce1 = context.ExtraNonce1;

        // build coinbase
        var coinbase = SerializeCoinbase(extraNonce1, extraNonce2);
        Span<byte> coinbaseHash = stackalloc byte[32];
        coinbaseHasher.Digest(coinbase, coinbaseHash);

        // hash block-header
        var headerBytes = SerializeHeader(coinbaseHash, nTime, nonce, context.VersionRollingMask, versionBits);
        Span<byte> headerHash = stackalloc byte[32];
        headerHasher.Digest(headerBytes, headerHash, (ulong) nTime, BlockTemplate, coin, networkParams);
        var headerValue = new uint256(headerHash);

        // calc share-diff
		var diff1 = coin.Diff1 != null ? BigInteger.Parse(coin.Diff1, NumberStyles.HexNumber) : BitcoinConstants.Diff1; // ?????Bitcoin??
		var shareDiff = (double) new BigRational(diff1, headerHash.ToBigInteger()) * shareMultiplier;
        var stratumDifficulty = context.Difficulty;
        var ratio = shareDiff / stratumDifficulty;

        // check if the share meets the much harder block difficulty (block candidate)
        var isBlockCandidate = headerValue <= blockTargetValue;

        // test if share meets at least workers current difficulty
        if(!isBlockCandidate && ratio < 0.99)
        {
            // check if share matched the previous difficulty from before a vardiff retarget
            if(context.VarDiff?.LastUpdate != null && context.PreviousDifficulty.HasValue)
            {
                ratio = shareDiff / context.PreviousDifficulty.Value;

                if(ratio < 0.99)
                    throw new StratumException(StratumError.LowDifficultyShare, $"low difficulty share ({shareDiff})");

                // use previous difficulty
                stratumDifficulty = context.PreviousDifficulty.Value;
            }

            else
                throw new StratumException(StratumError.LowDifficultyShare, $"low difficulty share ({shareDiff})");
        }

        var result = new Share
        {
            BlockHeight = BlockTemplate.Height,
            NetworkDifficulty = Difficulty,
            Difficulty = stratumDifficulty / shareMultiplier,
        };

        if(isBlockCandidate)
        {
            result.IsBlockCandidate = true;

            Span<byte> blockHash = stackalloc byte[32];
            blockHasher.Digest(headerBytes, blockHash, nTime);
            result.BlockHash = blockHash.ToHexString();

            var blockBytes = SerializeBlock(headerBytes, coinbase);
            var blockHex = blockBytes.ToHexString();

            return (result, blockHex);
        }

        return (result, null);
    }

    protected virtual byte[] SerializeCoinbase(string extraNonce1, string extraNonce2)
    {
        var extraNonce1Bytes = extraNonce1.HexToByteArray();
        var extraNonce2Bytes = extraNonce2.HexToByteArray();

        using(var stream = new MemoryStream())
        {
            stream.Write(coinbaseInitial);
            stream.Write(extraNonce1Bytes);
            stream.Write(extraNonce2Bytes);
            stream.Write(coinbaseFinal);

            return stream.ToArray();
        }
    }

    protected virtual byte[] SerializeBlock(byte[] header, byte[] coinbase)
    {
        var rawTransactionBuffer = BuildRawTransactionBuffer();
        var transactionCount = (uint) BlockTemplate.Transactions.Length + 1; // +1 for prepended coinbase tx

        using(var stream = new MemoryStream())
        {
            var bs = new BitcoinStream(stream, true);

            bs.ReadWrite(header);
            bs.ReadWriteAsVarInt(ref transactionCount);

            bs.ReadWrite(coinbase);
            bs.ReadWrite(rawTransactionBuffer);

            // POS coins require a zero byte appended to block which the daemon replaces with the signature
            if(isPoS)
                bs.ReadWrite((byte) 0);

            // if pool supports MWEB, we have to append the MWEB data to the block
            // https://github.com/litecoin-project/litecoin/blob/0.21/doc/mweb/mining-changes.md
            if(coin.HasMWEB)
            {
                var separator = new byte[] { 0x01 };
                var mweb = BlockTemplate.Extra.SafeExtensionDataAs<MwebBlockTemplateExtra>();
                if (mweb != null && mweb.Mweb != null) {
                     var mwebRaw = mweb.Mweb.HexToByteArray();
                     bs.ReadWrite(separator);
                     bs.ReadWrite(mwebRaw);
                 }
            }

            return stream.ToArray();
        }
    }

    protected virtual byte[] BuildRawTransactionBuffer()
    {
        using(var stream = new MemoryStream())
        {
            foreach(var tx in BlockTemplate.Transactions)
            {
                var txRaw = tx.Data.HexToByteArray();
                stream.Write(txRaw);
            }

            return stream.ToArray();
        }
    }

    #region Masternodes

    protected MasterNodeBlockTemplateExtra masterNodeParameters;

    protected virtual Money CreateMasternodeOutputs(Transaction tx, Money reward)
    {
        if(masterNodeParameters.Masternode != null)
        {
            Masternode[] masternodes;

            // Dash v13 Multi-Master-Nodes
            if(masterNodeParameters.Masternode.Type == JTokenType.Array)
                masternodes = masterNodeParameters.Masternode.ToObject<Masternode[]>();
            else
                masternodes = new[] { masterNodeParameters.Masternode.ToObject<Masternode>() };

            if(masternodes != null)
            {
                foreach(var masterNode in masternodes)
                {
                    if(!string.IsNullOrEmpty(masterNode.Script))
                    {
                        Script payeeAddress = new (masterNode.Script.HexToByteArray());
                        var payeeReward = masterNode.Amount;

                        tx.Outputs.Add(payeeReward, payeeAddress);
                        reward -= payeeReward;
                    }
                }
            }
        }

        if(masterNodeParameters.SuperBlocks is { Length: > 0 })
        {
            foreach(var superBlock in masterNodeParameters.SuperBlocks)
            {
                var payeeAddress = BitcoinUtils.AddressToDestination(superBlock.Payee, network);
                var payeeReward = superBlock.Amount;

                tx.Outputs.Add(payeeReward, payeeAddress);
                reward -= payeeReward;
            }
        }

        if(!coin.HasPayee && !string.IsNullOrEmpty(masterNodeParameters.Payee))
        {
            var payeeAddress = BitcoinUtils.AddressToDestination(masterNodeParameters.Payee, network);
            var payeeReward = masterNodeParameters.PayeeAmount;

            tx.Outputs.Add(payeeReward, payeeAddress);
            reward -= payeeReward;
        }

        return reward;
    }

    #endregion // Masternodes

    #region Community

    protected CommunityBlockTemplateExtra communityParameters;

    protected virtual Money CreateCommunityOutputs(Transaction tx, Money reward)
    {
        if (communityParameters.Community != null)
        {
            Community[] communitys;
            if (communityParameters.Community.Type == JTokenType.Array)
                communitys = communityParameters.Community.ToObject<Community[]>();
            else
                communitys = new[] { communityParameters.Community.ToObject<Community>() };

            if(communitys != null)
            {
                foreach(var Community in communitys)
                {
                    if(!string.IsNullOrEmpty(Community.Script))
                    {
                        Script payeeAddress = new (Community.Script.HexToByteArray());
                        var payeeReward = Community.Amount;

                        tx.Outputs.Add(payeeReward, payeeAddress);
                        reward -= payeeReward;
                    }
                }
            }
        }

        return reward;
    }

    #endregion //Community

    #region DataMining

    protected DataMiningBlockTemplateExtra dataminingParameters;

    protected virtual Money CreateDataMiningOutputs(Transaction tx, Money reward)
    {
        if (dataminingParameters.DataMining != null)
        {
            DataMining[] dataminings;
            if (dataminingParameters.DataMining.Type == JTokenType.Array)
                dataminings = dataminingParameters.DataMining.ToObject<DataMining[]>();
            else
                dataminings = new[] { dataminingParameters.DataMining.ToObject<DataMining>() };

            if(dataminings != null)
            {
                foreach(var DataMining in dataminings)
                {
                    if(!string.IsNullOrEmpty(DataMining.Script))
                    {
                        Script payeeAddress = new (DataMining.Script.HexToByteArray());
                        var payeeReward = DataMining.Amount;

                        tx.Outputs.Add(payeeReward, payeeAddress);
                        //reward -= payeeReward;
                    }
                }
            }
        }

        return reward;
    }

    #endregion //DataMining

    #region Developer

    protected DeveloperBlockTemplateExtra developerParameters;

    protected virtual Money CreateDeveloperOutputs(Transaction tx, Money reward)
    {
        if (developerParameters.Developer != null)
        {
            Developer[] developers;
            if (developerParameters.Developer.Type == JTokenType.Array)
                developers = developerParameters.Developer.ToObject<Developer[]>();
            else
                developers = new[] { developerParameters.Developer.ToObject<Developer>() };

            if(developers != null)
            {
                foreach(var Developer in developers)
                {
                    if(!string.IsNullOrEmpty(Developer.Script))
                    {
                        Script payeeAddress = new (Developer.Script.HexToByteArray());
                        var payeeReward = Developer.Amount;

                        tx.Outputs.Add(payeeReward, payeeAddress);
                        reward -= payeeReward;
                    }
                }
            }
        }

        return reward;
    }

    #endregion //Developer

    #region Founder

    protected FounderBlockTemplateExtra founderParameters;

    protected virtual Money CreateFounderOutputs(Transaction tx, Money reward)
    {
        if (founderParameters.Founder != null)
        {
            Founder[] founders;
            if (founderParameters.Founder.Type == JTokenType.Array)
                founders = founderParameters.Founder.ToObject<Founder[]>();
            else
                founders = new[] { founderParameters.Founder.ToObject<Founder>() };

            if(founders != null)
            {
                foreach(var Founder in founders)
                {
                    if(!string.IsNullOrEmpty(Founder.Script))
                    {
                        Script payeeAddress = new (Founder.Script.HexToByteArray());
                        var payeeReward = Founder.Amount;

                        tx.Outputs.Add(payeeReward, payeeAddress);
                        reward -= payeeReward;
                    }
                }
            }
        }

        return reward;
    }

    #endregion // Founder

    #region Minerfund

    protected MinerFundTemplateExtra minerFundParameters;

    protected virtual Money CreateMinerFundOutputs(Transaction tx, Money reward)
    {
        var payeeReward = minerFundParameters.MinimumValue;

        if (!string.IsNullOrEmpty(minerFundParameters.Addresses?.FirstOrDefault()))
        {
            var payeeAddress = BitcoinUtils.AddressToDestination(minerFundParameters.Addresses[0], network);
            tx.Outputs.Add(payeeReward, payeeAddress);
        }

        reward -= payeeReward;

        return reward;
    }

    #endregion // Minerfund


    #region MinerDevfund

    protected MinerDevFundTemplateExtra minerDevFundParameters;

    protected virtual Money CreateMinerDevFundOutputs(Transaction tx, Money reward)
    {
        var payeeReward = minerDevFundParameters.MinimumValue;

        if (!string.IsNullOrEmpty(minerDevFundParameters.Addresses?.FirstOrDefault()))
        {
            var payeeAddress = BitcoinUtils.AddressToDestination(minerDevFundParameters.Addresses[0], network);
            tx.Outputs.Add(payeeReward, payeeAddress);
        }

        reward -= payeeReward;

        return reward;
    }

    #endregion // MinerDevfund


    #region CommunityAddress

    protected virtual Money CreateCommunityAddressOutputs(Transaction tx, Money reward)
    {
        if(BlockTemplate.CommunityAutonomousValue > 0)
        {
            var payeeReward = BlockTemplate.CommunityAutonomousValue;
            var payeeAddress = BitcoinUtils.AddressToDestination(BlockTemplate.CommunityAutonomousAddress, network);
            tx.Outputs.Add(payeeReward, payeeAddress);
        }
        return reward;
    }
    #endregion // CommunityAddres

    #region CoinbaseDevReward

    protected CoinbaseDevRewardTemplateExtra CoinbaseDevRewardParams;

    protected virtual Money CreateCoinbaseDevRewardOutputs(Transaction tx, Money reward)
    {
        if(CoinbaseDevRewardParams.CoinbaseDevReward != null)
        {
            CoinbaseDevReward[] CBRewards;
            CBRewards = new[] { CoinbaseDevRewardParams.CoinbaseDevReward.ToObject<CoinbaseDevReward>() };

            foreach(var CBReward in CBRewards)
            {
                if(!string.IsNullOrEmpty(CBReward.ScriptPubkey))
                {
                    Script payeeAddress = new (CBReward.ScriptPubkey.HexToByteArray());
                    var payeeReward = CBReward.Value;
                    tx.Outputs.Add(payeeReward, payeeAddress);
                }
            }
        }
        return reward;
    }

    #endregion // CoinbaseDevReward

    #region Foundation

    protected FoundationBlockTemplateExtra foundationParameters;

    protected virtual Money CreateFoundationOutputs(Transaction tx, Money reward)
    {
        if(foundationParameters.Foundation != null)
        {
            Foundation[] foundations;
            if(foundationParameters.Foundation.Type == JTokenType.Array)
                foundations = foundationParameters.Foundation.ToObject<Foundation[]>();
            else
                foundations = new[] { foundationParameters.Foundation.ToObject<Foundation>() };

            if(foundations != null)
            {
                foreach(var Foundation in foundations)
                {
                    if(!string.IsNullOrEmpty(Foundation.Payee))
                    {
                        var payeeAddress = BitcoinUtils.AddressToDestination(Foundation.Payee, network);
                        var payeeReward = Foundation.Amount;

                        tx.Outputs.Add(payeeReward, payeeAddress);
                        reward -= payeeReward;
                    }
                }
            }
        }
        return reward;
    }

    #endregion // Foundation

    #region API-Surface

    public BlockTemplate BlockTemplate { get; protected set; }
    public double Difficulty { get; protected set; }

    public string JobId { get; protected set; }

    public void Init(BlockTemplate blockTemplate, string jobId,
        PoolConfig pc, BitcoinPoolConfigExtra extraPoolConfig,
        ClusterConfig cc, IMasterClock clock,
        IDestination poolAddressDestination, Network network,
        bool isPoS, double shareMultiplier, IHashAlgorithm coinbaseHasher,
        IHashAlgorithm headerHasher, IHashAlgorithm blockHasher)
    {
        Contract.RequiresNonNull(blockTemplate);
        Contract.RequiresNonNull(pc);
        Contract.RequiresNonNull(cc);
        Contract.RequiresNonNull(clock);
        Contract.RequiresNonNull(poolAddressDestination);
        Contract.RequiresNonNull(coinbaseHasher);
        Contract.RequiresNonNull(headerHasher);
        Contract.RequiresNonNull(blockHasher);
        Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(jobId));

        coin = pc.Template.As<BitcoinTemplate>();
        networkParams = coin.GetNetwork(network.ChainName);
        txVersion = coin.CoinbaseTxVersion;
        this.network = network;
        this.clock = clock;
        this.poolAddressDestination = poolAddressDestination;
        BlockTemplate = blockTemplate;
        JobId = jobId;

        var coinbaseString = !string.IsNullOrEmpty(cc.PaymentProcessing?.CoinbaseString) ?
            cc.PaymentProcessing?.CoinbaseString.Trim() : "Miningcore";

        scriptSigFinalBytes = new Script(Op.GetPushOp(Encoding.UTF8.GetBytes(coinbaseString))).ToBytes();

        Difficulty = new Target(System.Numerics.BigInteger.Parse(BlockTemplate.Target, NumberStyles.HexNumber)).Difficulty;

        extraNoncePlaceHolderLength = BitcoinConstants.ExtranoncePlaceHolderLength;
        this.isPoS = isPoS;
        this.shareMultiplier = shareMultiplier;

        txComment = !string.IsNullOrEmpty(extraPoolConfig?.CoinbaseTxComment) ?
            extraPoolConfig.CoinbaseTxComment : coin.CoinbaseTxComment;

        if(coin.HasMasterNodes)
        {
            masterNodeParameters = BlockTemplate.Extra.SafeExtensionDataAs<MasterNodeBlockTemplateExtra>();
			
            if(coin.HasSmartNodes)
            {
                if(masterNodeParameters.Extra?.ContainsKey("smartnode") == true)
                {
                    masterNodeParameters.Masternode = JToken.FromObject(masterNodeParameters.Extra["smartnode"]);
                }
            }

            if(!string.IsNullOrEmpty(masterNodeParameters.CoinbasePayload))
            {
                txVersion = 3;
                const uint txType = 5;
                txVersion += txType << 16;
            }
        }

        if(coin.HasPayee)
            payeeParameters = BlockTemplate.Extra.SafeExtensionDataAs<PayeeBlockTemplateExtra>();

        if(coin.HasCommunity)
            communityParameters = BlockTemplate.Extra.SafeExtensionDataAs<CommunityBlockTemplateExtra>();

        if(coin.HasDataMining)
            dataminingParameters = BlockTemplate.Extra.SafeExtensionDataAs<DataMiningBlockTemplateExtra>();

        if(coin.HasDeveloper)
            developerParameters = BlockTemplate.Extra.SafeExtensionDataAs<DeveloperBlockTemplateExtra>();

        if (coin.HasFounderFee)
        {
            founderParameters = BlockTemplate.Extra.SafeExtensionDataAs<FounderBlockTemplateExtra>();

            if(coin.Symbol == "FTB")
            {
                if(founderParameters.Extra?.ContainsKey("fortune") == true)
                {
                    founderParameters.Founder = JToken.FromObject(founderParameters.Extra["fortune"]);
                }
            }

            if(coin.HasDevFee)
            {
                if(founderParameters.Extra?.ContainsKey("devfee") == true)
                {
                    founderParameters.Founder = JToken.FromObject(founderParameters.Extra["devfee"]);
                }
            }
        }

        if (coin.HasMinerDevFund)
            minerDevFundParameters = BlockTemplate.Extra.SafeExtensionDataAs<MinerDevFundTemplateExtra>("coinbasetxn", "minerdevfund");

        if (coin.HasMinerFund)
            minerFundParameters = BlockTemplate.Extra.SafeExtensionDataAs<MinerFundTemplateExtra>("coinbasetxn", "minerfund");

        if (coin.HasCoinbaseDevReward)
            CoinbaseDevRewardParams = BlockTemplate.Extra.SafeExtensionDataAs<CoinbaseDevRewardTemplateExtra>();

        if (coin.HasFoundation)
            foundationParameters = BlockTemplate.Extra.SafeExtensionDataAs<FoundationBlockTemplateExtra>();

        this.coinbaseHasher = coinbaseHasher;
        this.headerHasher = headerHasher;
        this.blockHasher = blockHasher;

        if(!string.IsNullOrEmpty(BlockTemplate.Target))
            blockTargetValue = new uint256(BlockTemplate.Target);
        else
        {
            var tmp = new Target(BlockTemplate.Bits.HexToByteArray());
            blockTargetValue = tmp.ToUInt256();
        }

        previousBlockHashReversedHex = BlockTemplate.PreviousBlockhash
            .HexToByteArray()
            .ReverseByteOrder()
            .ToHexString();

        BuildMerkleBranches();
        BuildCoinbase();

        jobParams = new object[]
        {
            JobId,
            previousBlockHashReversedHex,
            coinbaseInitialHex,
            coinbaseFinalHex,
            merkleBranchesHex,
            BlockTemplate.Version.ToStringHex8(),
            BlockTemplate.Bits,
            BlockTemplate.CurTime.ToStringHex8(),
            false
        };
    }

    public object GetJobParams(bool isNew)
    {
        jobParams[^1] = isNew;
        return jobParams;
    }

    public virtual (Share Share, string BlockHex) ProcessShare(StratumConnection worker,
        string extraNonce2, string nTime, string nonce, string versionBits = null)
    {
        Contract.RequiresNonNull(worker);
        Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(extraNonce2));
        Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(nTime));
        Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(nonce));

        var context = worker.ContextAs<BitcoinWorkerContext>();

        // validate nTime
        if(nTime.Length != 8)
            throw new StratumException(StratumError.Other, "incorrect size of ntime");

        var nTimeInt = uint.Parse(nTime, NumberStyles.HexNumber);
        if(nTimeInt < BlockTemplate.CurTime || nTimeInt > ((DateTimeOffset) clock.Now).ToUnixTimeSeconds() + 7200)
            throw new StratumException(StratumError.Other, "ntime out of range");

        // validate nonce
        if(nonce.Length != 8)
            throw new StratumException(StratumError.Other, "incorrect size of nonce");

        var nonceInt = uint.Parse(nonce, NumberStyles.HexNumber);

        // validate version-bits (overt ASIC boost)
        uint versionBitsInt = 0;

        if(context.VersionRollingMask.HasValue && versionBits != null)
        {
            versionBitsInt = uint.Parse(versionBits, NumberStyles.HexNumber);

            // enforce that only bits covered by current mask are changed by miner
            if((versionBitsInt & ~context.VersionRollingMask.Value) != 0)
                throw new StratumException(StratumError.Other, "rolling-version mask violation");
        }

        // dupe check
        if(!RegisterSubmit(context.ExtraNonce1, extraNonce2, nTime, nonce))
            throw new StratumException(StratumError.DuplicateShare, "duplicate share");

        return ProcessShareInternal(worker, extraNonce2, nTimeInt, nonceInt, versionBitsInt);
    }

    #endregion // API-Surface
}