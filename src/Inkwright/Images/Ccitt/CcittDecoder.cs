namespace Inkwright.Images.Ccitt;

/// <summary>How a fax stream is coded, taken from the <c>K</c> parameter of ISO 32000-2 Table 11.</summary>
/// <param name="Columns">Pixels across a row.</param>
/// <param name="Rows">Rows expected, or zero when the data says where it ends.</param>
/// <param name="K">Below zero for group 4, zero for one-dimensional group 3, above for mixed.</param>
/// <param name="EndOfLine">Whether end-of-line patterns are guaranteed present.</param>
/// <param name="ByteAligned">Whether each row starts on a byte boundary.</param>
/// <param name="BlackIs1">Whether a one bit means black, reversing the usual PDF convention.</param>
/// <param name="DamagedRowsBeforeError">How many unreadable rows to tolerate before giving up.</param>
internal readonly record struct CcittOptions(
    int Columns,
    int Rows,
    int K,
    bool EndOfLine,
    bool ByteAligned,
    bool BlackIs1,
    int DamagedRowsBeforeError);

/// <summary>
/// The fax codings of ITU-T T.4 and T.6. A row is stored not as pixels but as the positions where
/// the colour changes, which is what makes the two-dimensional modes cheap: a row is usually almost
/// the same as the one above it, so it is coded as the differences between their changing elements.
/// </summary>
internal sealed class CcittDecoder
{
    /// <summary>An upper bound on rows, so a stream that never ends cannot spin forever.</summary>
    private const int MaxRows = 1 << 20;

    private readonly ReadOnlyMemory<byte> _data;
    private readonly CcittOptions _options;

    /// <summary>Where the colour changes in the row above, terminated by two entries at the width.</summary>
    private int[] _reference;

    /// <summary>The same for the row being decoded.</summary>
    private int[] _current;

    private int _referenceCount;
    private int _currentCount;

    private int _position;
    private int _bit;

    private CcittDecoder(ReadOnlyMemory<byte> data, CcittOptions options)
    {
        _data = data;
        _options = options;

        _reference = new int[options.Columns + 3];
        _current = new int[options.Columns + 3];

        // An imaginary all-white row sits above the first one.
        _reference[0] = options.Columns;
        _reference[1] = options.Columns;
        _referenceCount = 2;
    }

    /// <summary>Decodes fax data into one bit per pixel, rows padded to a byte boundary.</summary>
    /// <param name="data">The coded data.</param>
    /// <param name="options">The parameters of Table 11.</param>
    /// <returns>The rows, with a set bit meaning black before <c>BlackIs1</c> is applied.</returns>
    /// <exception cref="InvalidDataException">The stream is damaged beyond what the parameters tolerate.</exception>
    internal static byte[] Decode(ReadOnlySpan<byte> data, CcittOptions options) =>
        Decode(data, options, out _);

    /// <summary>Decodes fax data and reports how many bytes it took.</summary>
    /// <param name="data">The coded data.</param>
    /// <param name="options">The parameters of Table 11.</param>
    /// <param name="consumed">
    /// Receives the number of bytes read, counting any end-of-block pattern. A caller that packs
    /// several images into one stream — as the grey-scale planes of a halftone region do — needs
    /// this to find where the next one starts.
    /// </param>
    /// <exception cref="InvalidDataException">The stream is damaged beyond what the parameters tolerate.</exception>
    internal static byte[] Decode(ReadOnlySpan<byte> data, CcittOptions options, out int consumed)
    {
        if (options.Columns is < 1 or > 1 << 16)
            throw new InvalidDataException($"A fax image {options.Columns} pixels wide cannot be decoded.");

        var decoder = new CcittDecoder(data.ToArray(), options);
        byte[] rows = decoder.Run();

        decoder.SkipEndOfBlock();
        consumed = decoder.Exhausted ? data.Length : decoder._position + (decoder._bit > 0 ? 1 : 0);

        return rows;
    }

    /// <summary>
    /// Steps over the end-of-facsimile-block pattern, which is two end-of-line patterns in a row.
    /// It is optional wherever the length is known ahead of time and required where it is not.
    /// </summary>
    private void SkipEndOfBlock()
    {
        int save = _position;
        int saveBit = _bit;

        for (int i = 0; i < 2; i++)
        {
            int zeros = 0;
            while (!Exhausted && ReadBit() == 0)
                zeros++;

            if (zeros < CcittCodes.EndOfLineLength - 1)
            {
                _position = save;
                _bit = saveBit;
                return;
            }
        }
    }

    private byte[] Run()
    {
        int stride = (_options.Columns + 7) / 8;
        int expected = _options.Rows > 0 ? _options.Rows : 0;

        var rows = new List<byte[]>(expected > 0 ? expected : 64);
        bool twoDimensional = _options.K < 0;
        int damaged = 0;

        while (rows.Count < MaxRows && (expected == 0 || rows.Count < expected))
        {
            if (_options.ByteAligned && _options.K >= 0)
                AlignToByte();

            SkipFill();

            if (Exhausted)
                break;

            // In mixed mode a one-bit tag after each end-of-line says how the next row is coded.
            if (_options.K > 0)
                twoDimensional = ReadBit() == 0;

            bool decoded = twoDimensional ? TryDecodeTwoDimensional() : TryDecodeOneDimensional();

            if (!decoded)
            {
                if (++damaged > _options.DamagedRowsBeforeError)
                    break;

                // A damaged row is replaced by the one above it, or by white when there is none.
                rows.Add(rows.Count > 0 ? (byte[])rows[^1].Clone() : new byte[stride]);
                continue;
            }

            rows.Add(Emit(stride));
            Swap();

            if (_options.ByteAligned && _options.K < 0)
                AlignToByte();
        }

        return Assemble(rows, stride, expected);
    }

    private byte[] Assemble(List<byte[]> rows, int stride, int expected)
    {
        int height = expected > 0 ? expected : rows.Count;
        byte[] output = new byte[stride * Math.Max(height, 1)];

        for (int y = 0; y < height && y < rows.Count; y++)
            rows[y].CopyTo(output, y * stride);

        return output;
    }

    /// <summary>Turns the changing-element positions of a row into bits, black meaning one.</summary>
    private byte[] Emit(int stride)
    {
        byte[] row = new byte[stride];

        // Positions alternate colour starting from white, so every second span is black.
        int start = 0;
        for (int i = 0; i < _currentCount; i++)
        {
            int end = Math.Min(_current[i], _options.Columns);

            if ((i & 1) == 1)
            {
                for (int x = start; x < end; x++)
                    row[x >> 3] |= (byte)(0x80 >> (x & 7));
            }

            start = end;
            if (start >= _options.Columns)
                break;
        }

        return row;
    }

    /// <summary>Reads one row coded as alternating runs of white and black (T.4 clause 4.1).</summary>
    private bool TryDecodeOneDimensional()
    {
        _currentCount = 0;
        int position = 0;
        bool white = true;

        while (position < _options.Columns)
        {
            if (!TryReadRun(white, out int run))
                return false;

            position = Math.Min(position + run, _options.Columns);
            Append(position);
            white = !white;
        }

        Terminate();
        return true;
    }

    /// <summary>
    /// Reads one row coded against the row above it (T.4 clause 4.2, T.6 clause 2). Each code says
    /// where the next colour change is relative to the changes above, which is why a page of text
    /// compresses so far: most rows differ from their neighbour in only a few places.
    /// </summary>
    private bool TryDecodeTwoDimensional()
    {
        _currentCount = 0;
        int a0 = -1;
        bool white = true;

        while (a0 < _options.Columns)
        {
            if (!TryReadMode(out CcittMode mode, out int offset))
                return false;

            switch (mode)
            {
                case CcittMode.Pass:
                {
                    // The run continues past the change above; no colour change is recorded.
                    a0 = FindB(a0, white, second: true);
                    break;
                }

                case CcittMode.Vertical:
                {
                    int change = Math.Clamp(FindB(a0, white, second: false) + offset, 0, _options.Columns);
                    Append(change);
                    a0 = change;
                    white = !white;
                    break;
                }

                case CcittMode.Horizontal:
                {
                    if (!TryReadRun(white, out int first) || !TryReadRun(!white, out int second))
                        return false;

                    int start = a0 < 0 ? 0 : a0;
                    int middle = Math.Min(start + first, _options.Columns);
                    int end = Math.Min(middle + second, _options.Columns);

                    Append(middle);
                    Append(end);
                    a0 = end;
                    break;
                }

                case CcittMode.EndOfLine:
                    return _currentCount > 0 && Finish();

                default:
                    return false;
            }

            if (_currentCount > _options.Columns + 1)
                return false;
        }

        return Finish();
    }

    private bool Finish()
    {
        Terminate();
        return true;
    }

    /// <summary>
    /// The first changing element on the row above that lies to the right of the current position
    /// and has the opposite colour of the run being coded, or the one after it.
    /// </summary>
    private int FindB(int a0, bool white, bool second)
    {
        int index = 0;

        // Changing elements alternate colour from white, so the parity of the index is the colour.
        while (index < _referenceCount && _reference[index] <= a0)
            index++;

        while (index < _referenceCount && (index & 1) != (white ? 0 : 1))
            index++;

        if (second)
            index++;

        return index < _referenceCount ? _reference[index] : _options.Columns;
    }

    private void Append(int position)
    {
        if (_currentCount < _current.Length)
            _current[_currentCount++] = position;
    }

    /// <summary>Closes a row with two entries at the width, which the lookahead relies on.</summary>
    private void Terminate()
    {
        Append(_options.Columns);
        Append(_options.Columns);
    }

    private void Swap()
    {
        (_reference, _current) = (_current, _reference);
        _referenceCount = _currentCount;
        _currentCount = 0;
    }

    private bool TryReadRun(bool white, out int total)
    {
        total = 0;
        CcittRunTable table = CcittCodes.Runs(white);

        // A run longer than 63 is spelled as make-up codes and one terminating code.
        for (int guard = 0; guard < 64; guard++)
        {
            if (!TryReadCode(table, out int run))
                return false;

            total += run;

            if (run < 64)
                return true;
        }

        return false;
    }

    private bool TryReadCode(CcittRunTable table, out int run)
    {
        int code = 0;

        for (int length = 1; length <= CcittCodes.MaxCodeLength; length++)
        {
            if (Exhausted)
                break;

            code = (code << 1) | ReadBit();

            if (table.TryGet(length, code, out run))
                return true;
        }

        run = 0;
        return false;
    }

    private bool TryReadMode(out CcittMode mode, out int offset)
    {
        int code = 0;

        for (int length = 1; length <= CcittCodes.MaxCodeLength; length++)
        {
            if (Exhausted)
                break;

            code = (code << 1) | ReadBit();

            if (CcittCodes.ModeCodes.TryGet(length, code, out mode, out offset))
                return true;
        }

        mode = CcittMode.Pass;
        offset = 0;
        return false;
    }

    /// <summary>
    /// Steps over any end-of-line patterns and the fill bits that pad up to them. They are optional
    /// in a PDF stream but a decoder has to accept them wherever they appear.
    /// </summary>
    private void SkipFill()
    {
        while (!Exhausted)
        {
            int save = _position;
            int saveBit = _bit;

            int zeros = 0;
            while (!Exhausted && ReadBit() == 0)
                zeros++;

            if (Exhausted)
                return;

            // Eleven or more zeros then a one is an end-of-line; anything else was real data.
            if (zeros >= CcittCodes.EndOfLineLength - 1)
                continue;

            _position = save;
            _bit = saveBit;
            return;
        }
    }

    private void AlignToByte()
    {
        if (_bit == 0)
            return;

        _bit = 0;
        _position++;
    }

    private bool Exhausted => _position >= _data.Length;

    private int ReadBit()
    {
        if (Exhausted)
            return 0;

        int value = (_data.Span[_position] >> (7 - _bit)) & 1;

        if (++_bit == 8)
        {
            _bit = 0;
            _position++;
        }

        return value;
    }
}
