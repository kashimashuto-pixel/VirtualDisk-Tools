namespace Qcow2Explorer.Core;

// Derived from AxioDL/lzokay, Copyright (c) 2018 Jack Andersen, MIT License.
internal static class Lzo1xDecoder
{
    public static int Decompress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (source.Length < 3)
        {
            throw new InvalidDataException("LZO1Xブロックが短すぎます。");
        }

        var input = 0;
        var output = 0;
        var state = 0;
        var matchLength = 0;

        if (source[input] >= 22)
        {
            var length = source[input++] - 17;
            CopyLiterals(source, destination, ref input, ref output, length);
            state = 4;
        }
        else if (source[input] >= 18)
        {
            state = source[input++] - 17;
            CopyLiterals(source, destination, ref input, ref output, state);
        }

        while (true)
        {
            RequireInput(source, input, 1);
            var instruction = source[input++];
            int match;
            int nextState;

            if ((instruction & 0xc0) != 0)
            {
                RequireInput(source, input, 1);
                match = output - ((source[input++] << 3) + ((instruction >> 2) & 0x7) + 1);
                matchLength = (instruction >> 5) + 1;
                nextState = instruction & 0x3;
            }
            else if ((instruction & 0x20) != 0)
            {
                matchLength = (instruction & 0x1f) + 2;
                if (matchLength == 2)
                {
                    matchLength += ReadExtendedLength(source, ref input, 31);
                }

                RequireInput(source, input, 2);
                var encoded = source[input] | (source[input + 1] << 8);
                input += 2;
                match = output - ((encoded >> 2) + 1);
                nextState = encoded & 0x3;
            }
            else if ((instruction & 0x10) != 0)
            {
                matchLength = (instruction & 0x7) + 2;
                if (matchLength == 2)
                {
                    matchLength += ReadExtendedLength(source, ref input, 7);
                }

                RequireInput(source, input, 2);
                var encoded = source[input] | (source[input + 1] << 8);
                input += 2;
                match = output - (((instruction & 0x8) << 11) + (encoded >> 2));
                nextState = encoded & 0x3;
                if (match == output)
                {
                    break;
                }

                match -= 16384;
            }
            else if (state == 0)
            {
                var length = instruction + 3;
                if (length == 3)
                {
                    length += ReadExtendedLength(source, ref input, 15);
                }

                CopyLiterals(source, destination, ref input, ref output, length);
                state = 4;
                continue;
            }
            else if (state != 4)
            {
                RequireInput(source, input, 1);
                nextState = instruction & 0x3;
                match = output - ((instruction >> 2) + (source[input++] << 2) + 1);
                matchLength = 2;
            }
            else
            {
                RequireInput(source, input, 1);
                nextState = instruction & 0x3;
                match = output - ((instruction >> 2) + (source[input++] << 2) + 2049);
                matchLength = 3;
            }

            if (match < 0)
            {
                throw new InvalidDataException("LZO1Xの参照距離が展開済みデータの範囲外です。");
            }

            RequireInput(source, input, nextState);
            RequireOutput(destination, output, checked(matchLength + nextState));
            for (var index = 0; index < matchLength; index++)
            {
                destination[output++] = destination[match++];
            }

            state = nextState;
            for (var index = 0; index < nextState; index++)
            {
                destination[output++] = source[input++];
            }
        }

        if (matchLength != 3)
        {
            throw new InvalidDataException("LZO1Xの終端マーカーが不正です。");
        }

        if (input != source.Length)
        {
            throw new InvalidDataException(
                input < source.Length
                    ? "LZO1Xブロックの終端後に未使用データがあります。"
                    : "LZO1Xブロックの入力を超えて読み取りました。");
        }

        return output;
    }

    private static int ReadExtendedLength(ReadOnlySpan<byte> source, ref int input, int basis)
    {
        var zeroCount = 0;
        while (true)
        {
            RequireInput(source, input, 1);
            var value = source[input++];
            if (value != 0)
            {
                return checked(basis + zeroCount * 255 + value);
            }

            zeroCount++;
        }
    }

    private static void CopyLiterals(
        ReadOnlySpan<byte> source,
        Span<byte> destination,
        ref int input,
        ref int output,
        int length)
    {
        RequireInput(source, input, length);
        RequireOutput(destination, output, length);
        source.Slice(input, length).CopyTo(destination[output..]);
        input += length;
        output += length;
    }

    private static void RequireInput(ReadOnlySpan<byte> source, int offset, int count)
    {
        if (count < 0 || offset < 0 || offset > source.Length - count)
        {
            throw new InvalidDataException("LZO1Xブロックの入力が途中で終了しました。");
        }
    }

    private static void RequireOutput(Span<byte> destination, int offset, int count)
    {
        if (count < 0 || offset < 0 || offset > destination.Length - count)
        {
            throw new InvalidDataException("LZO1Xブロックの展開サイズが宣言値を超えました。");
        }
    }
}
