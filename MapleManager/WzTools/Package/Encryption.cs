﻿using System;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace MapleManager.WzTools.Package
{
    interface IWzEncryption
    {
        void Decrypt(byte[] bytes);
        void Encrypt(byte[] bytes);
    }

    abstract class BaseAesEncryption
    {
        private readonly SymmetricAlgorithm _algo = new AesManaged();
        private readonly ICryptoTransform _transformer;
        private const int BlockSize = 16;
        private byte[] _xorKey = new byte[BlockSize];

        protected BaseAesEncryption(byte[] userKey, byte[] iv)
        {
            // It looks like maple has OFB implemented
            // However, that is not an option in C# for AES
            // ECB is the closest version we can get, still need to do the XOR ourselves
            _algo.Mode = CipherMode.ECB;
            _algo.Padding = PaddingMode.None;
            _algo.Key = userKey;

            _transformer = _algo.CreateEncryptor();

            // IV is not actually used for AES? Just for the start of the initial block
            InitFirstBlock(iv);
        }

        private void InitFirstBlock(byte[] iv)
        {
            byte[] freshIvBlock = new byte[BlockSize] {
                iv[0], iv[1], iv[2], iv[3],
                iv[0], iv[1], iv[2], iv[3],
                iv[0], iv[1], iv[2], iv[3],
                iv[0], iv[1], iv[2], iv[3]
            };

            // Initialize the first block with the IV
            _transformer.TransformBlock(freshIvBlock, 0, BlockSize, _xorKey, 0);
        }

        private static int GetNextXorKeySize(int leastSize)
        {
            int x = (leastSize / BlockSize) + 1;
            x *= BlockSize;
            return x;
        }

        public void Prepare(int requiredXorKeyLength)
        {
            if (_xorKey.Length > requiredXorKeyLength) return;

            int newSize = GetNextXorKeySize(requiredXorKeyLength);
            int previousSize = _xorKey.Length;
            Array.Resize(ref _xorKey, newSize);

            for (var offset = previousSize; offset < newSize; offset += BlockSize)
            {
                _transformer.TransformBlock(_xorKey, (offset - BlockSize), BlockSize, _xorKey, offset);
            }
        }

        /// <summary>
        /// XOR the input data with the xor key.
        /// Calling Prepare() is not required.
        /// </summary>
        /// <param name="input"></param>
        public void XorData(byte[] input)
        {
            var inputLength = input.Length;
            Prepare(inputLength);

            const int bigChunkSize = sizeof(UInt64);

            unsafe
            {
                fixed (byte* dataPtr = input)
                fixed (byte* xorPtr = _xorKey)
                {
                    byte* currentInputByte = dataPtr;
                    byte* currentXorByte = xorPtr;

                    var i = 0;

                    int intBlocks = inputLength / bigChunkSize;
                    for (; i < intBlocks; ++i)
                    {
                        *(UInt64*)currentInputByte ^= *(UInt64*)currentXorByte;
                        currentInputByte += bigChunkSize;
                        currentXorByte += bigChunkSize;
                    }

                    i *= bigChunkSize;

                    for (; i < inputLength; i++)
                    {
                        *(currentInputByte++) ^= *(currentXorByte++);
                    }
                }
            }
        }
    }

    class DefaultUserKeyWzEncryption : BaseAesEncryption, IWzEncryption
    {
        public static readonly byte[] DefaultAesUserKey = new byte[]
        {
            0x13, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x06, 0x00, 0x00, 0x00, 0xB4, 0x00, 0x00, 0x00,
            0x1B, 0x00, 0x00, 0x00, 0x0F, 0x00, 0x00, 0x00, 0x33, 0x00, 0x00, 0x00, 0x52, 0x00, 0x00, 0x00
        };

        public DefaultUserKeyWzEncryption(byte[] iv) : base(DefaultAesUserKey, iv)
        {

        }
        
        public void Decrypt(byte[] bytes)
        {
            XorData(bytes);
        }

        public void Encrypt(byte[] bytes)
        {
            XorData(bytes);
        }
    }

    class GmsWzEncryption : DefaultUserKeyWzEncryption
    {
        public GmsWzEncryption() : base(new byte[] { 0x4D, 0x23, 0xC7, 0x2B })
        {
        }
    }

    class SeaWzEncryption : DefaultUserKeyWzEncryption
    {
        public SeaWzEncryption() : base(new byte[] { 0xB9, 0x7D, 0x63, 0xE9 })
        {
        }
    }

    class NopWzEncryption : IWzEncryption
    {
        public void Decrypt(byte[] bytes)
        {
        }

        public void Encrypt(byte[] bytes)
        {
        }
    }

    static class WzEncryption
    {
        private static IWzEncryption currentEncryption = null;
        private static IWzEncryption nopEncryption = new NopWzEncryption();

        private static IWzEncryption[] cryptos =
        {
            nopEncryption,
            new GmsWzEncryption(),
            new SeaWzEncryption(),
        };

        private static byte[] GetCopy(byte[] input)
        {
            var x = new byte[input.Length];
            input.CopyTo(x, 0);
            return x;
        }

        private static void PutCurrentInFront()
        {
            var x = cryptos[0];
            cryptos[0] = currentEncryption;
            for (var i = 1; i < cryptos.Length; i++)
            {
                if (cryptos[i] == currentEncryption)
                {
                    cryptos[i] = x;
                    break;
                }
            }
        }

        public static bool HasCurrentCrypto => currentEncryption != null;

        public static void TryDecryptImage(byte[] contents)
        {
            // I'm not going to try and crack it
            // This is because a Property should already be succesfully decoded, setting currentEncryption.
            if (currentEncryption != null)
            {
                currentEncryption.Decrypt(contents);
            }
        }

        private static void TrySetNopAsDefault()
        {
            if (currentEncryption != null) return;
            currentEncryption = nopEncryption;
            PutCurrentInFront();
        }

        public static void TryDecryptString(byte[] contents, Func<byte[], bool> validate)
        {
            if (validate != null && validate(contents))
            {
                TrySetNopAsDefault();
                return;
            }

            for (var i = 0; i < cryptos.Length; i++)
            {
                var copy = GetCopy(contents);
                var crypto = cryptos[i];

                crypto.Decrypt(copy);
                if (validate == null || validate(copy))
                {
                    var isChanged = validate != null && crypto != currentEncryption;

                    if (isChanged)
                    {
                        Console.WriteLine("Found crypto {0}", crypto);
                        Console.WriteLine("\tString ASCII: {0}", Encoding.ASCII.GetString(contents));
                        Console.WriteLine("\tString UTF8: {0}", Encoding.UTF8.GetString(contents));
                        Console.WriteLine("Changed to...");
                        Console.WriteLine("\tString ASCII: {0}", Encoding.ASCII.GetString(copy));
                        Console.WriteLine("\tString UTF8: {0}", Encoding.UTF8.GetString(copy));
                    }

                    // seems to have worked
                    copy.CopyTo(contents, 0);

                    if (isChanged)
                    {
                       
                        currentEncryption = crypto;
                        
                        PutCurrentInFront();
                    }

                    return;
                }
            }

            Console.WriteLine("No crypto found for string!");
            Console.WriteLine("String ASCII: {0}", Encoding.ASCII.GetString(contents));
            Console.WriteLine("String UTF8: {0}", Encoding.UTF8.GetString(contents));

        }

        public static void ApplyCrypto(byte[] contents)
        {
            TrySetNopAsDefault();
            currentEncryption.Encrypt(contents);
        }
    }
}
