using System.Collections;
using System.Runtime.InteropServices;

namespace Inkwright.Cos;

/// <summary>
/// A PDF array object (ISO 32000-1 §7.3.6). Elements are stored contiguously and can be read as a
/// span; typed accessors resolve indirect references when the array came from a file.
/// </summary>
public sealed class PdfArray : IReadOnlyList<PdfValue>
{
    private readonly List<PdfValue> _items;

    /// <summary>Creates an empty array.</summary>
    public PdfArray() => _items = [];

    /// <summary>Creates an empty array with room for <paramref name="capacity"/> elements.</summary>
    /// <param name="capacity">The number of elements to preallocate.</param>
    public PdfArray(int capacity) => _items = new List<PdfValue>(capacity);

    /// <summary>Creates an array holding the given elements.</summary>
    /// <param name="items">The elements to copy into the array.</param>
    public PdfArray(params ReadOnlySpan<PdfValue> items)
    {
        _items = new List<PdfValue>(items.Length);
        _items.AddRange(items);
    }

    /// <summary>Creates an array of numbers, the shape used by rectangles, matrices and ranges.</summary>
    /// <param name="numbers">The numbers to store.</param>
    public static PdfArray OfNumbers(params ReadOnlySpan<double> numbers)
    {
        var array = new PdfArray(numbers.Length);
        foreach (double number in numbers)
            array.Add(PdfValue.Number(number));

        return array;
    }

    internal IPdfResolver? Resolver { get; set; }

    /// <inheritdoc />
    public int Count => _items.Count;

    /// <summary>Gets or sets an element without resolving indirect references.</summary>
    /// <param name="index">The zero-based element index.</param>
    public PdfValue this[int index]
    {
        get => _items[index];
        set
        {
            PdfResolverBinder.Bind(value, Resolver);
            _items[index] = value;
        }
    }

    /// <summary>The elements as a contiguous span, for allocation-free scanning.</summary>
    public ReadOnlySpan<PdfValue> AsSpan() => CollectionsMarshal.AsSpan(_items);

    /// <summary>Appends an element.</summary>
    /// <param name="value">The element to append.</param>
    public void Add(PdfValue value)
    {
        PdfResolverBinder.Bind(value, Resolver);
        _items.Add(value);
    }

    /// <summary>Appends several elements.</summary>
    /// <param name="values">The elements to append.</param>
    public void AddRange(params ReadOnlySpan<PdfValue> values)
    {
        foreach (PdfValue value in values)
            PdfResolverBinder.Bind(value, Resolver);

        _items.AddRange(values);
    }

    /// <summary>Inserts an element at the given position.</summary>
    /// <param name="index">The zero-based position to insert at.</param>
    /// <param name="value">The element to insert.</param>
    public void Insert(int index, PdfValue value)
    {
        PdfResolverBinder.Bind(value, Resolver);
        _items.Insert(index, value);
    }

    /// <summary>Removes the element at the given position.</summary>
    /// <param name="index">The zero-based position to remove.</param>
    public void RemoveAt(int index) => _items.RemoveAt(index);

    /// <summary>Removes the first occurrence of a value.</summary>
    /// <param name="value">The value to remove.</param>
    /// <returns>Whether an element was removed.</returns>
    public bool Remove(PdfValue value) => _items.Remove(value);

    /// <summary>Removes every element.</summary>
    public void Clear() => _items.Clear();

    /// <summary>Reports whether the array contains a value.</summary>
    /// <param name="value">The value to look for.</param>
    public bool Contains(PdfValue value) => _items.Contains(value);

    /// <summary>Gets an element with indirect references resolved.</summary>
    /// <param name="index">The zero-based element index.</param>
    public PdfValue Get(int index)
    {
        PdfValue value = _items[index];
        return value.IsReference && Resolver is not null ? Resolver.Resolve(value) : value;
    }

    /// <summary>Gets a numeric element, or <paramref name="fallback"/> when it is missing or not numeric.</summary>
    /// <param name="index">The zero-based element index.</param>
    /// <param name="fallback">The value to return when the element is absent or not a number.</param>
    public double GetNumber(int index, double fallback = 0)
    {
        if ((uint)index >= (uint)_items.Count)
            return fallback;

        PdfValue value = Get(index);
        return value.IsNumber ? value.AsNumber : fallback;
    }

    /// <summary>Gets an integer element, or <paramref name="fallback"/> when it is missing or not numeric.</summary>
    /// <param name="index">The zero-based element index.</param>
    /// <param name="fallback">The value to return when the element is absent or not a number.</param>
    public long GetInteger(int index, long fallback = 0)
    {
        if ((uint)index >= (uint)_items.Count)
            return fallback;

        PdfValue value = Get(index);
        return value.IsNumber ? value.AsInteger : fallback;
    }

    /// <summary>Gets a name element, or <see langword="null"/> when it is missing or not a name.</summary>
    /// <param name="index">The zero-based element index.</param>
    public PdfName? GetName(int index) =>
        (uint)index < (uint)_items.Count ? Get(index).AsName : null;

    /// <summary>Gets a dictionary element, or <see langword="null"/> when it is missing or not a dictionary.</summary>
    /// <param name="index">The zero-based element index.</param>
    public PdfDictionary? GetDictionary(int index) =>
        (uint)index < (uint)_items.Count ? Get(index).AsDictionary : null;

    /// <summary>Gets an array element, or <see langword="null"/> when it is missing or not an array.</summary>
    /// <param name="index">The zero-based element index.</param>
    public PdfArray? GetArray(int index) =>
        (uint)index < (uint)_items.Count ? Get(index).AsArray : null;

    /// <summary>Gets a stream element, or <see langword="null"/> when it is missing or not a stream.</summary>
    /// <param name="index">The zero-based element index.</param>
    public PdfStream? GetStream(int index) =>
        (uint)index < (uint)_items.Count ? Get(index).AsStream : null;

    /// <summary>Copies the numeric elements into a freshly allocated array.</summary>
    public double[] ToNumberArray()
    {
        double[] numbers = new double[_items.Count];
        for (int i = 0; i < numbers.Length; i++)
            numbers[i] = GetNumber(i);

        return numbers;
    }

    /// <inheritdoc />
    public IEnumerator<PdfValue> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <inheritdoc />
    public override string ToString() => $"[{_items.Count} items]";
}
