namespace CFRezManager;

internal static class LithTechLtcNativeDecoder
{
    private const int HeaderByteCount = 4;
    private const int WindowSize = 4096;
    private const int WindowMask = WindowSize - 1;
    private const int MinMatchLength = 2;
    private const int MaxDecodedBytes = 256 * 1024 * 1024;

    public static bool TryDecode(byte[] data, out byte[] decoded, out string? errorMessage)
    {
        decoded = [];
        errorMessage = null;

        if (data.Length < HeaderByteCount + 2)
        {
            errorMessage = LocalizedText.T("LtcDataTooShort");
            return false;
        }

        if (data[0] != 0 || data[1] != 0 || data[2] != 0 || data[3] != 0)
        {
            errorMessage = LocalizedText.T("LtcHeaderInvalid");
            return false;
        }

        int estimatedCapacity = data.Length > int.MaxValue / 4 ? 4 * 1024 * 1024 : data.Length * 4;
        int initialCapacity = Math.Min(4 * 1024 * 1024, Math.Max(estimatedCapacity, 256));
        var output = new List<byte>(initialCapacity);
        byte[] window = new byte[WindowSize];
        Array.Fill(window, (byte)' ');

        int writePosition = 0;
        var reader = new BitReader(data, HeaderByteCount);

        while (reader.HasData)
        {
            int flag = reader.ReadBit();
            if (flag == 1)
            {
                if (reader.RemainingBits < 8)
                {
                    errorMessage = LocalizedText.T("LtcLiteralIncomplete");
                    return false;
                }

                byte value = (byte)reader.ReadBits(8);
                if (!TryEmit(value, output, window, ref writePosition, out errorMessage))
                {
                    return false;
                }

                continue;
            }

            int offset = reader.ReadBitsZeroPadded(12);
            if (offset == 0)
            {
                decoded = output.ToArray();
                return true;
            }

            if (reader.RemainingBits < 4)
            {
                errorMessage = LocalizedText.T("LtcMatchLengthIncomplete");
                return false;
            }

            int length = reader.ReadBits(4) + MinMatchLength;
            int sourcePosition = (offset - 1) & WindowMask;
            for (int i = 0; i < length; i++)
            {
                byte value = window[(sourcePosition + i) & WindowMask];
                if (!TryEmit(value, output, window, ref writePosition, out errorMessage))
                {
                    return false;
                }
            }
        }

        errorMessage = LocalizedText.T("LtcNoEndMarker");
        return false;
    }

    private static bool TryEmit(
        byte value,
        List<byte> output,
        byte[] window,
        ref int writePosition,
        out string? errorMessage)
    {
        if (output.Count >= MaxDecodedBytes)
        {
            errorMessage = LocalizedText.T("LtcDecodedTooLarge");
            return false;
        }

        output.Add(value);
        window[writePosition] = value;
        writePosition = (writePosition + 1) & WindowMask;
        errorMessage = null;
        return true;
    }

    private sealed class BitReader
    {
        private readonly byte[] _data;
        private int _bitPosition;

        public BitReader(byte[] data, int byteOffset)
        {
            _data = data;
            _bitPosition = byteOffset * 8;
        }

        public bool HasData => _bitPosition < _data.Length * 8;

        public int RemainingBits => Math.Max(0, _data.Length * 8 - _bitPosition);

        public int ReadBit()
        {
            int byteIndex = _bitPosition >> 3;
            int bitIndex = _bitPosition & 7;
            _bitPosition++;
            return (_data[byteIndex] >> bitIndex) & 1;
        }

        public int ReadBits(int bitCount)
        {
            int value = 0;
            for (int i = 0; i < bitCount; i++)
            {
                value = (value << 1) | ReadBit();
            }

            return value;
        }

        public int ReadBitsZeroPadded(int bitCount)
        {
            int value = 0;
            for (int i = 0; i < bitCount; i++)
            {
                int bit = HasData ? ReadBit() : 0;
                value = (value << 1) | bit;
            }

            return value;
        }
    }
}
