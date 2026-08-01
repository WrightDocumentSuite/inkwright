using System.Collections;

namespace Inkwright.Cos;

/// <summary>
/// A PDF dictionary object (ISO 32000-1 §7.3.7). Entries keep insertion order so that rewritten
/// files stay byte-stable; lookups are linear for the small dictionaries that dominate real files
/// and switch to a hash index once a dictionary grows.
/// </summary>
public sealed class PdfDictionary : IEnumerable<KeyValuePair<PdfName, PdfValue>>
{
    private const int IndexThreshold = 12;

    private PdfName[] _keys;
    private PdfValue[] _values;
    private int _count;
    private Dictionary<PdfName, int>? _index;

    /// <summary>Creates an empty dictionary.</summary>
    public PdfDictionary() : this(4)
    {
    }

    /// <summary>Creates an empty dictionary with room for <paramref name="capacity"/> entries.</summary>
    /// <param name="capacity">The number of entries to preallocate.</param>
    public PdfDictionary(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _keys = capacity == 0 ? [] : new PdfName[capacity];
        _values = capacity == 0 ? [] : new PdfValue[capacity];
    }

    internal IPdfResolver? Resolver { get; set; }

    /// <summary>The number of entries.</summary>
    public int Count => _count;

    /// <summary>The keys in insertion order.</summary>
    public ReadOnlySpan<PdfName> Keys => _keys.AsSpan(0, _count);

    /// <summary>
    /// Gets or sets an entry without resolving indirect references. Reading a missing key yields
    /// the null object; assigning the null object removes the key, matching §7.3.9.
    /// </summary>
    /// <param name="key">The entry key.</param>
    public PdfValue this[PdfName key]
    {
        get => TryGetRaw(key, out PdfValue value) ? value : PdfValue.Null;
        set
        {
            if (value.IsNull)
                Remove(key);
            else
                Set(key, value);
        }
    }

    /// <summary>Gets or sets an entry by name text.</summary>
    /// <param name="key">The entry key without its leading solidus.</param>
    public PdfValue this[string key]
    {
        get => this[PdfName.Get(key)];
        set => this[PdfName.Get(key)] = value;
    }

    /// <summary>Stores an entry, replacing any existing value for the key.</summary>
    /// <param name="key">The entry key.</param>
    /// <param name="value">The value to store, including the null object.</param>
    public void Set(PdfName key, PdfValue value)
    {
        ArgumentNullException.ThrowIfNull(key);
        PdfResolverBinder.Bind(value, Resolver);

        int slot = IndexOf(key);
        if (slot >= 0)
        {
            _values[slot] = value;
            return;
        }

        if (_count == _keys.Length)
            Grow();

        _keys[_count] = key;
        _values[_count] = value;
        _index?.Add(key, _count);
        _count++;

        if (_index is null && _count > IndexThreshold)
            BuildIndex();
    }

    /// <summary>Stores an entry only when it is absent.</summary>
    /// <param name="key">The entry key.</param>
    /// <param name="value">The value to store.</param>
    /// <returns>Whether the entry was added.</returns>
    public bool TryAdd(PdfName key, PdfValue value)
    {
        if (ContainsKey(key))
            return false;

        Set(key, value);
        return true;
    }

    /// <summary>Removes an entry.</summary>
    /// <param name="key">The entry key.</param>
    /// <returns>Whether an entry was removed.</returns>
    public bool Remove(PdfName key)
    {
        int slot = IndexOf(key);
        if (slot < 0)
            return false;

        for (int i = slot; i < _count - 1; i++)
        {
            _keys[i] = _keys[i + 1];
            _values[i] = _values[i + 1];
        }

        _count--;
        _keys[_count] = null!;
        _values[_count] = PdfValue.Null;
        _index = null;

        if (_count > IndexThreshold)
            BuildIndex();

        return true;
    }

    /// <summary>Reports whether the dictionary holds an entry for the key.</summary>
    /// <param name="key">The entry key.</param>
    public bool ContainsKey(PdfName key) => IndexOf(key) >= 0;

    /// <summary>Reads an entry without resolving indirect references.</summary>
    /// <param name="key">The entry key.</param>
    /// <param name="value">Receives the stored value.</param>
    public bool TryGetRaw(PdfName key, out PdfValue value)
    {
        int slot = IndexOf(key);
        if (slot < 0)
        {
            value = PdfValue.Null;
            return false;
        }

        value = _values[slot];
        return true;
    }

    /// <summary>Reads an entry with indirect references resolved.</summary>
    /// <param name="key">The entry key.</param>
    /// <returns>The resolved value, or the null object when the key is absent.</returns>
    public PdfValue Get(PdfName key)
    {
        if (!TryGetRaw(key, out PdfValue value))
            return PdfValue.Null;

        return value.IsReference && Resolver is not null ? Resolver.Resolve(value) : value;
    }

    /// <summary>Reads an entry with indirect references resolved.</summary>
    /// <param name="key">The entry key without its leading solidus.</param>
    public PdfValue Get(string key) => Get(PdfName.Get(key));

    /// <summary>Reads the first entry that is present, in the order given.</summary>
    /// <param name="keys">The candidate keys, most specific first.</param>
    public PdfValue GetFirst(params ReadOnlySpan<PdfName> keys)
    {
        foreach (PdfName key in keys)
        {
            if (ContainsKey(key))
                return Get(key);
        }

        return PdfValue.Null;
    }

    /// <summary>Reads a name entry.</summary>
    /// <param name="key">The entry key.</param>
    public PdfName? GetName(PdfName key) => Get(key).AsName;

    /// <summary>Reads a dictionary entry, including the dictionary of a stream.</summary>
    /// <param name="key">The entry key.</param>
    public PdfDictionary? GetDictionary(PdfName key) => Get(key).AsDictionary;

    /// <summary>Reads an array entry.</summary>
    /// <param name="key">The entry key.</param>
    public PdfArray? GetArray(PdfName key) => Get(key).AsArray;

    /// <summary>Reads a stream entry.</summary>
    /// <param name="key">The entry key.</param>
    public PdfStream? GetStream(PdfName key) => Get(key).AsStream;

    /// <summary>Reads a numeric entry.</summary>
    /// <param name="key">The entry key.</param>
    /// <param name="fallback">The value to return when the entry is absent or not numeric.</param>
    public double GetNumber(PdfName key, double fallback = 0)
    {
        PdfValue value = Get(key);
        return value.IsNumber ? value.AsNumber : fallback;
    }

    /// <summary>Reads an integer entry.</summary>
    /// <param name="key">The entry key.</param>
    /// <param name="fallback">The value to return when the entry is absent or not numeric.</param>
    public long GetInteger(PdfName key, long fallback = 0)
    {
        PdfValue value = Get(key);
        return value.IsNumber ? value.AsInteger : fallback;
    }

    /// <summary>Reads a boolean entry.</summary>
    /// <param name="key">The entry key.</param>
    /// <param name="fallback">The value to return when the entry is absent or not a boolean.</param>
    public bool GetBoolean(PdfName key, bool fallback = false)
    {
        PdfValue value = Get(key);
        return value.Kind == PdfValueKind.Boolean ? value.AsBoolean : fallback;
    }

    /// <summary>Reads a string entry and decodes it as text.</summary>
    /// <param name="key">The entry key.</param>
    public string? GetTextString(PdfName key) => Get(key).AsTextString;

    /// <summary>
    /// Reads an entry that the specification allows to be either a single item or an array of them,
    /// such as <c>Filter</c> or <c>Contents</c>, and yields the items in order.
    /// </summary>
    /// <param name="key">The entry key.</param>
    public IEnumerable<PdfValue> GetAsSequence(PdfName key)
    {
        PdfValue value = Get(key);
        if (value.IsNull)
            yield break;

        if (value.AsArray is { } array)
        {
            for (int i = 0; i < array.Count; i++)
                yield return array.Get(i);
        }
        else
        {
            yield return value;
        }
    }

    /// <summary>Creates a shallow copy: the same values under the same keys, in the same order.</summary>
    public PdfDictionary Clone()
    {
        var copy = new PdfDictionary(_count) { Resolver = Resolver };
        for (int i = 0; i < _count; i++)
            copy.Set(_keys[i], _values[i]);

        return copy;
    }

    /// <inheritdoc />
    public Enumerator GetEnumerator() => new(this);

    IEnumerator<KeyValuePair<PdfName, PdfValue>> IEnumerable<KeyValuePair<PdfName, PdfValue>>.GetEnumerator()
    {
        for (int i = 0; i < _count; i++)
            yield return new KeyValuePair<PdfName, PdfValue>(_keys[i], _values[i]);
    }

    IEnumerator IEnumerable.GetEnumerator() =>
        ((IEnumerable<KeyValuePair<PdfName, PdfValue>>)this).GetEnumerator();

    /// <inheritdoc />
    public override string ToString()
    {
        PdfName? type = GetName(PdfNames.Type);
        return type is null ? $"<<{_count} entries>>" : $"<</Type/{type.Value} … {_count} entries>>";
    }

    private int IndexOf(PdfName key)
    {
        if (_index is not null)
            return _index.TryGetValue(key, out int slot) ? slot : -1;

        PdfName[] keys = _keys;
        for (int i = 0; i < _count; i++)
        {
            if (ReferenceEquals(keys[i], key) || keys[i].Equals(key))
                return i;
        }

        return -1;
    }

    private void BuildIndex()
    {
        var index = new Dictionary<PdfName, int>(_count);
        for (int i = 0; i < _count; i++)
            index[_keys[i]] = i;

        _index = index;
    }

    private void Grow()
    {
        int capacity = _keys.Length == 0 ? 4 : _keys.Length * 2;
        System.Array.Resize(ref _keys, capacity);
        System.Array.Resize(ref _values, capacity);
    }

    /// <summary>Walks the entries in insertion order without allocating.</summary>
    public struct Enumerator
    {
        private readonly PdfDictionary _dictionary;
        private int _position;

        internal Enumerator(PdfDictionary dictionary)
        {
            _dictionary = dictionary;
            _position = -1;
        }

        /// <summary>The entry at the current position.</summary>
        public readonly KeyValuePair<PdfName, PdfValue> Current =>
            new(_dictionary._keys[_position], _dictionary._values[_position]);

        /// <summary>Advances to the next entry.</summary>
        /// <returns>Whether another entry was available.</returns>
        public bool MoveNext() => ++_position < _dictionary._count;
    }
}
