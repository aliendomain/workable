using System.Collections;

namespace Workable;

/// <summary>
/// Base type for simple list-style query results.
/// </summary>
/// <typeparam name="TItem">The item type returned by the query.</typeparam>
/// <param name="Items">The items contained in the query result.</param>
public abstract record WorkQueryListResult<TItem>(IReadOnlyList<TItem> Items) :
    IReadOnlyList<TItem>,
    IWorkQueryResult
{
    /// <summary>
    /// Gets the number of items in the result.
    /// </summary>
    public int Count => this.Items.Count;

    /// <summary>
    /// Gets the item at the specified index.
    /// </summary>
    /// <param name="index">The zero-based index of the item to access.</param>
    public TItem this[int index] => this.Items[index];

    /// <summary>
    /// Returns a typed enumerator for the result items.
    /// </summary>
    /// <returns>An enumerator over <see cref="Items"/>.</returns>
    public IEnumerator<TItem> GetEnumerator()
        => this.Items.GetEnumerator();

    /// <summary>
    /// Returns a non-generic enumerator for the result items.
    /// </summary>
    /// <returns>An enumerator over <see cref="Items"/>.</returns>
    IEnumerator IEnumerable.GetEnumerator()
        => this.GetEnumerator();
}
